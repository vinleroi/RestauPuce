using ChefOS.Core.Common;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Les chiffres d'une période : ce que le chef gagne (chiffre d'affaires HT, coût théorique et marge par plat, menu engineering) et ce qu'il perd (pertes
/// déclarées, écart entre la consommation réelle et la consommation théorique de deux inventaires clôturés).
/// Tout est lu en une fois, pour toute la période (et l'intervalle des inventaires) : ingrédients, historique des prix, recettes et leurs lignes, ventes,
/// commandes, pertes, inventaires et lignes de tickets ; aucune requête par plat. Le contexte est filtré par restaurant.
/// Les jours sont ceux du restaurant (son fuseau) : une ligne de commande compte pour le jour local de sa création. Les montants ne sont pas arrondis.
/// </summary>
public class StatsService(IDbContextFactory<ChefOsDbContext> factory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private const string InvalidPeriodMessage = "La période est invalide";
    private const string PeriodTooLongMessage = "Période trop longue (366 jours maximum)";

    /// <summary>Durée maximale d'une période, bornes comprises.</summary>
    private const int MaxPeriodDays = 366;

    /// <summary>
    /// Échelle de la marge unitaire passée au menu engineering : les marges dérivées d'un rendement ont une vingtaine de décimales, et deux marges
    /// mathématiquement égales (ou une marge égale à la moyenne) peuvent alors différer au dernier chiffre et perdre leur égalité au seuil.
    /// </summary>
    private const int MarginScale = 6;

    /// <summary>
    /// Les chiffres des jours UTC <paramref name="fromDay"/> à <paramref name="toDay"/> inclus (l'heure éventuelle est ignorée).
    /// <list type="bullet">
    /// <item><description>
    /// <b>Ventes</b> : les saisies (<see cref="SalesEntry"/>) dont le jour est dans la période, plus les lignes des commandes non annulées créées dans la période.
    /// CA HT = Σ quantité × prix figé ÷ (1 + TVA figée). Une saisie de quantité nulle ou négative est ignorée. Un total de semaine (daté du lundi) compte
    /// pour toute période qui contient ce lundi : dans le CA, les coûts et les portions ; <see cref="PeriodStats.RevenueFromEntries"/> le comprend, mais
    /// <see cref="PeriodStats.RevenueByDay"/> l'exclut et <see cref="PeriodStats.WeeklyTotalsRevenue"/> le totalise. Chaque jour de la période figure dans
    /// <see cref="PeriodStats.RevenueByDay"/>, même à 0.
    /// </description></item>
    /// <item><description>
    /// <b>Coûts</b> : aux prix en vigueur au dernier jour de la période (<see cref="PriceAsOf"/>) ; un prix postérieur est ignoré. Le coût d'un plat est le
    /// <see cref="CostResult.TotalCost"/> de sa recette ; un coût incomplet entre quand même dans le total, qui devient incomplet. Une recette sans aucune ligne (ou
    /// qui utilise une préparation sans ligne) n'a pas un coût nul : son coût est inconnu, donc incomplet. Seuls les plats vendus et complets entrent dans la matrice
    /// de menu engineering (la marge unitaire y est arrondie à 6 décimales).
    /// </description></item>
    /// <item><description>
    /// <b>Pertes</b> : chacune au prix de sa date (voir <see cref="WasteCosting"/>), regroupées par motif. Une perte au coût inconnu est comptée sans coût ;
    /// un motif (et la période : <see cref="PeriodStats.WasteCostIncomplete"/>) est signalé incomplet dès qu'une de ses pertes a un coût inconnu ou partiel.
    /// </description></item>
    /// <item><description>
    /// <b>Écart d'inventaire</b> : entre l'inventaire clôturé le plus récent à la date de début ou avant (A) et le premier inventaire clôturé à la date de fin ou
    /// après (B) ; sans les deux (ou si A et B tombent le même jour, l'intervalle étant alors vide), il n'y a ni écart ni coût réel. L'intervalle ]A, B] est
    /// indépendant de la période. Achats = lignes non ignorées des tickets appliqués, avec ingrédient, datés dans ]A, B] ; la date d'un ticket est sa date lue,
    /// ou à défaut le jour local de sa création (comme la date du prix qu'il a posé). Consommation théorique = décomposition des ventes et des pertes de ]A, B].
    /// Valorisation au prix du jour B.
    /// Seuls les ingrédients comptés aux deux inventaires ont une ligne d'écart (un ingrédient non compté à l'un serait pris pour un stock nul et ferait inventer des
    /// pertes) ; ceux qui ont bougé dans l'intervalle ou n'ont été comptés qu'à un des deux sont nommés dans <see cref="VarianceReport.NotCountedIngredients"/>.
    /// Le total des écarts et le coût réel (<see cref="PeriodStats.ActualFoodCost"/> = Σ consommation réelle × prix à B, les ingrédients sans prix n'y comptant pas,
    /// ÷ CA HT de ]A, B]) ne portent donc que sur les lignes comptées : ce dernier ignore la consommation des ingrédients non comptés, et
    /// <see cref="PeriodStats.ActualFoodCostPartial"/> le signale dès qu'un de ces ingrédients a été acheté ou consommé (théoriquement) dans l'intervalle.
    /// <see cref="VarianceReport.Warnings"/> signale ce que les consommations théorique et réelle sous-estiment : recettes dont la décomposition est incomplète ou qui n'ont
    /// aucune ligne, pertes d'ingrédient dont l'unité ne se convertit plus, lignes de ticket dont l'unité ne se convertit plus (écartées des achats).
    /// Approximation : un total de semaine n'est pas ventilé par jour, il compte dans ]A, B] si son lundi y est.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <exception cref="DomainValidationException">Bornes inversées, ou période de plus de 366 jours.</exception>
    public async Task<PeriodStats> GetAsync(DateTime fromDay, DateTime toDay)
    {
        var from = UtcDays.Of(fromDay);
        var to = UtcDays.Of(toDay);
        if (to < from)
            throw new DomainValidationException(InvalidPeriodMessage);
        if ((to - from).TotalDays >= MaxPeriodDays)
            throw new DomainValidationException(PeriodTooLongMessage);

        await using var db = await factory.CreateDbContextAsync();
        var calendar = await RestaurantCalendar.ForCurrentRestaurantAsync(db, _clock);

        var window = await FindVarianceWindowAsync(db, from, to);
        var data = await LoadAsync(db, calendar, from, to, window);

        var sales = data.Sales;
        var periodSales = sales.Where(s => s.Day >= from && s.Day <= to).ToList();

        var dishes = ComputeDishes(data, periodSales, to);
        var theoreticalCost = dishes.Sum(d => d.TotalCost ?? 0m);
        var revenueFromEntries = periodSales.Where(s => !s.FromOrder).Sum(s => s.RevenueExclVat);
        var revenueFromOrders = periodSales.Where(s => s.FromOrder).Sum(s => s.RevenueExclVat);
        var revenue = revenueFromEntries + revenueFromOrders;

        var waste = ComputeWaste(data, from, to);

        VarianceReport? variance = null;
        decimal? actualFoodCost = null;
        if (window is not null)
            (variance, actualFoodCost) = ComputeVariance(data, window);

        return new PeriodStats(
            from,
            to,
            revenue,
            revenueFromEntries,
            revenueFromOrders,
            periodSales.Where(s => s.IsWeeklyTotal).Sum(s => s.RevenueExclVat),
            theoreticalCost,
            dishes.Any(d => d.CostIncomplete),
            revenue - theoreticalCost,
            revenue == 0m ? null : theoreticalCost / revenue,
            waste.Sum(w => w.Cost),
            waste.Any(w => w.CostIncomplete),
            actualFoodCost,
            variance?.ActualFoodCostPartial ?? false,
            ComputeRevenueByDay(periodSales, from, to),
            dishes,
            waste,
            variance);
    }

    // ── Chargement ───────────────────────────────────────────────────────────

    /// <summary>
    /// Les inventaires qui encadrent la période : A, le plus récent des clôturés dont le jour est ≤ <paramref name="from"/> (à jour égal, le dernier créé), et
    /// B, le premier des clôturés dont le jour est ≥ <paramref name="to"/> (à jour égal, le premier créé). Null s'il en manque un, ou si B n'est pas après A.
    /// </summary>
    private static async Task<VarianceWindow?> FindVarianceWindowAsync(ChefOsDbContext db, DateTime from, DateTime to)
    {
        var opening = await db.StockCounts.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.Status == StockCountStatus.Cloture && c.Date < from.AddDays(1))
            .OrderByDescending(c => c.Date).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync();
        if (opening is null)
            return null;

        var closing = await db.StockCounts.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.Status == StockCountStatus.Cloture && c.Date >= to)
            .OrderBy(c => c.Date).ThenBy(c => c.Id)
            .FirstOrDefaultAsync();
        if (closing is null)
            return null;

        var openingDay = UtcDays.Of(opening.Date);
        var closingDay = UtcDays.Of(closing.Date);
        return closingDay > openingDay ? new VarianceWindow(openingDay, closingDay, opening, closing) : null;
    }

    private static async Task<Dataset> LoadAsync(ChefOsDbContext db, RestaurantCalendar calendar, DateTime from, DateTime to, VarianceWindow? window)
    {
        // Les ventes, pertes et prix sont lus une fois pour la période et pour l'intervalle des inventaires, qui peut la déborder des deux côtés.
        var start = window is null ? from : Min(from, window.After.AddDays(1));
        var endExclusive = (window is null ? to : Max(to, window.Through)).AddDays(1);

        // Les commandes sont des instants : on les cherche entre minuit local du premier jour et minuit local du lendemain du dernier.
        var startInstant = calendar.UtcBounds(start).FromUtc;
        var endInstant = calendar.UtcBounds(endExclusive).FromUtc;

        var ingredients = await db.Ingredients.AsNoTracking().ToListAsync();
        var history = (await db.IngredientPrices.AsNoTracking().Where(p => p.Date < endExclusive).ToListAsync()).ToLookup(p => p.IngredientId);
        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Lines.OrderBy(l => l.Id)).ToListAsync();
        var recipeIds = recipes.Select(r => r.Id).ToHashSet();

        var entries = await db.SalesEntries.AsNoTracking()
            .Where(e => e.Date >= start && e.Date < endExclusive && e.Quantity > 0m)
            .Select(e => new { e.RecipeId, e.Date, e.IsWeeklyTotal, e.Quantity, e.UnitPriceInclVat, e.VatRate })
            .ToListAsync();
        var orderLines = await (
                from line in db.OrderLines.AsNoTracking()
                join order in db.Orders.AsNoTracking() on line.OrderId equals order.Id
                where order.Status != OrderStatus.Annulee && order.CreatedAt >= startInstant && order.CreatedAt < endInstant && line.Quantity > 0
                select new { order.CreatedAt, line.RecipeId, line.Quantity, line.UnitPriceInclVat, line.VatRate })
            .ToListAsync();

        // Un plat supprimé entre deux lectures n'a plus de nom ni de coût : ses ventes sont écartées partout, pour que les totaux restent cohérents.
        var sales = entries
            .Select(e => new Sale(e.RecipeId, UtcDays.Of(e.Date), e.IsWeeklyTotal, FromOrder: false, e.Quantity, Revenue(e.Quantity, e.UnitPriceInclVat, e.VatRate)))
            .Concat(orderLines.Select(l => new Sale(l.RecipeId, calendar.DayOf(l.CreatedAt), IsWeeklyTotal: false, FromOrder: true, l.Quantity, Revenue(l.Quantity, l.UnitPriceInclVat, l.VatRate))))
            .Where(s => recipeIds.Contains(s.RecipeId))
            .ToList();

        var waste = await db.WasteEntries.AsNoTracking()
            .Where(e => e.Date >= start && e.Date < endExclusive)
            .ToListAsync();

        var purchases = new List<PurchaseLine>();
        if (window is not null)
        {
            var firstDay = window.After.AddDays(1);
            var lastDayExclusive = window.Through.AddDays(1);
            var firstInstant = calendar.UtcBounds(firstDay).FromUtc;
            var lastInstantExclusive = calendar.UtcBounds(lastDayExclusive).FromUtc;
            var lines = await (
                    from line in db.ReceiptLines.AsNoTracking()
                    join receipt in db.Receipts.AsNoTracking() on line.ReceiptId equals receipt.Id
                    where receipt.Status == ReceiptStatus.Applied
                          && ((receipt.ReceiptDate != null && receipt.ReceiptDate >= firstDay && receipt.ReceiptDate < lastDayExclusive)
                              || (receipt.ReceiptDate == null && receipt.CreatedAt >= firstInstant && receipt.CreatedAt < lastInstantExclusive))
                          && !line.IsIgnored && line.IngredientId != null
                    select new { line.IngredientId, line.Quantity, line.Unit, line.LineTotalExclVat, line.RawLabel, receipt.ReceiptDate, receipt.CreatedAt })
                .ToListAsync();
            purchases = lines
                .Select(l => new PurchaseLine(l.IngredientId!.Value, l.Quantity, l.Unit, l.LineTotalExclVat, l.RawLabel, l.ReceiptDate is { } readDay ? UtcDays.Of(readDay) : calendar.DayOf(l.CreatedAt)))
                .ToList();
        }

        return new Dataset(ingredients, history, recipes, sales, waste, purchases);
    }

    // ── Ventes, plats et chiffre d'affaires ──────────────────────────────────

    private static List<DishProfit> ComputeDishes(Dataset data, List<Sale> periodSales, DateTime to)
    {
        // Coûts aux prix du dernier jour de la période, un seul livre pour tous les plats.
        var book = new RecipeBook(data.Recipes, PriceAsOf.WithPricesAt(data.Ingredients, data.History, to));
        var namesById = data.Recipes.ToDictionary(r => r.Id, r => r.Name);
        var gaps = new RecipeGaps(data.Recipes);

        var rows = new List<DishProfit>();
        var menuItems = new List<MenuItem>();
        foreach (var group in periodSales.GroupBy(s => s.RecipeId))
        {
            var quantity = group.Sum(s => s.Quantity);
            var revenue = group.Sum(s => s.RevenueExclVat);
            var cost = book.Cost(group.Key);
            var totalCost = cost.TotalCost * quantity;

            // Une recette sans aucune ligne coûte « 0 » sans être complète : son coût est inconnu.
            var costIncomplete = !cost.IsComplete || gaps.IsHollow(group.Key);

            rows.Add(new DishProfit(
                group.Key,
                namesById[group.Key],
                quantity,
                group.Where(s => !s.FromOrder).Sum(s => s.Quantity),
                group.Where(s => s.FromOrder).Sum(s => s.Quantity),
                revenue,
                cost.TotalCost,
                totalCost,
                revenue - totalCost,
                revenue == 0m ? null : totalCost / revenue,
                costIncomplete,
                Class: null));

            // Un plat au coût partiel aurait une marge fausse : il reste hors de la matrice.
            if (!costIncomplete)
                menuItems.Add(new MenuItem(group.Key, quantity, Math.Round((revenue - totalCost) / quantity, MarginScale, MidpointRounding.AwayFromZero)));
        }

        var classes = MenuEngineering.Classify(menuItems).ToDictionary(c => c.RecipeId, c => (MenuClass?)c.Class);

        return rows
            .Select(r => r with { Class = classes.GetValueOrDefault(r.RecipeId) })
            .OrderByDescending(r => r.RevenueExclVat)
            .ThenBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(r => r.RecipeId)
            .ToList();
    }

    private static List<DayRevenue> ComputeRevenueByDay(List<Sale> periodSales, DateTime from, DateTime to)
    {
        // Les totaux de semaine n'ont pas de jour de vente : ils ne figurent pas ici.
        var byDay = periodSales.Where(s => !s.IsWeeklyTotal).ToLookup(s => s.Day);

        var days = new List<DayRevenue>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var sales = byDay[day].ToList();
            days.Add(new DayRevenue(
                day,
                sales.Where(s => !s.FromOrder).Sum(s => s.RevenueExclVat),
                sales.Where(s => s.FromOrder).Sum(s => s.RevenueExclVat)));
        }

        return days;
    }

    // ── Pertes ───────────────────────────────────────────────────────────────

    private static List<WasteByReason> ComputeWaste(Dataset data, DateTime from, DateTime to)
    {
        var costing = new WasteCosting(data.Recipes, data.Ingredients, data.History);

        return data.Waste
            .Where(e => UtcDays.Of(e.Date) >= from && UtcDays.Of(e.Date) <= to)
            .Select(e => (e.Reason, Valuation: costing.Value(e)))
            .Where(x => x.Valuation is not null)
            .GroupBy(x => x.Reason)
            .Select(g => new WasteByReason(
                g.Key,
                g.Sum(x => x.Valuation!.Value.Cost ?? 0m),
                g.Count(),
                g.Any(x => x.Valuation!.Value.CostIncomplete || x.Valuation!.Value.Cost is null)))
            .OrderByDescending(w => w.Cost)
            .ThenBy(w => w.Reason)
            .ToList();
    }

    // ── Écart d'inventaire ───────────────────────────────────────────────────

    private static (VarianceReport Report, decimal? ActualFoodCost) ComputeVariance(Dataset data, VarianceWindow window)
    {
        var ingredientsById = data.Ingredients.ToDictionary(i => i.Id);
        var opening = CountedQuantities(window.Opening);
        var closing = CountedQuantities(window.Closing);
        var purchased = PurchasedQuantities(data.Purchases, ingredientsById);
        var purchases = purchased.Totals;

        var windowSales = data.Sales.Where(s => s.Day > window.After && s.Day <= window.Through).ToList();
        var windowWaste = data.Waste.Where(e => UtcDays.Of(e.Date) > window.After && UtcDays.Of(e.Date) <= window.Through).ToList();
        var consumption = TheoreticalConsumption(data, windowSales, windowWaste, ingredientsById);
        var theoretical = consumption.Totals;

        var priceAtClose = PriceAsOf.WithPricesAt(data.Ingredients, data.History, window.Through)
            .ToDictionary(i => i.Id, i => i.CurrentPricePerBaseUnit);

        // Un ingrédient absent d'un inventaire serait pris pour un stock nul (ouverture ou clôture à 0) et ferait inventer un écart énorme : seuls ceux comptés
        // aux deux ont une ligne. Les autres, s'ils ont bougé dans l'intervalle ou n'ont été comptés qu'à un des deux, sont nommés à part.
        var countedInBoth = opening.Keys.Intersect(closing.Keys).ToHashSet();
        var notCounted = opening.Keys.Concat(closing.Keys).Concat(purchases.Keys).Concat(theoretical.Keys)
            .Distinct()
            .Where(id => !countedInBoth.Contains(id) && ingredientsById.ContainsKey(id))
            .Select(id => ingredientsById[id].Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Le coût réel ignore ces ingrédients : s'il y en a un d'utilisé, il est sous-estimé.
        var actualFoodCostPartial = purchases.Concat(theoretical)
            .Any(q => q.Value > 0m && !countedInBoth.Contains(q.Key) && ingredientsById.ContainsKey(q.Key));

        Dictionary<int, decimal> Counted(Dictionary<int, decimal> quantities) =>
            quantities.Where(q => countedInBoth.Contains(q.Key)).ToDictionary(q => q.Key, q => q.Value);

        var lines = InventoryVariance.Compute(Counted(opening), Counted(purchases), Counted(closing), Counted(theoretical), priceAtClose);

        // Un ingrédient supprimé entre deux lectures n'a plus de nom : sa ligne est écartée.
        var rows = lines
            .Where(l => ingredientsById.ContainsKey(l.IngredientId))
            .Select(l =>
            {
                var ingredient = ingredientsById[l.IngredientId];
                return new VarianceRow(
                    l.IngredientId, ingredient.Name, ingredient.BaseUnit, l.Opening, l.Purchases, l.Closing,
                    l.ActualUse, l.TheoreticalUse, l.VarianceQuantity, l.VarianceValue);
            })
            .ToList();

        var warnings = consumption.Warnings.Concat(purchased.Warnings).Distinct().ToList();
        var report = new VarianceReport(
            window.After, window.Through, rows, rows.Sum(r => r.VarianceValue ?? 0m), notCounted, actualFoodCostPartial, warnings);

        // Coût réel : la consommation réelle (des lignes comptées) valorisée au prix du jour B, rapportée au CA de l'intervalle ; sans CA ou sans aucun prix connu, il est inconnu.
        var windowRevenue = windowSales.Sum(s => s.RevenueExclVat);
        var priced = rows.Where(r => priceAtClose.GetValueOrDefault(r.IngredientId) is not null).ToList();
        decimal? actualFoodCost = windowRevenue == 0m || priced.Count == 0
            ? null
            : priced.Sum(r => r.ActualUse * priceAtClose[r.IngredientId]!.Value) / windowRevenue;

        return (report, actualFoodCost);
    }

    private static Dictionary<int, decimal> CountedQuantities(StockCount count) =>
        count.Lines
            .GroupBy(l => l.IngredientId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.BaseQuantity));

    /// <summary>
    /// Les quantités achetées, en unité de base. La quantité ne dépend pas du prix : une ligne dont seul le total est invalide (un problème de prix) est
    /// quand même une livraison, on la compte ; une ligne sans quantité exploitable est écartée, et une ligne dont l'unité ne se convertit plus dans l'unité de base
    /// de l'ingrédient l'est aussi, avec un avertissement (sans doublon, triés par date puis libellé) : ces achats manquent à la consommation réelle.
    /// </summary>
    private static Purchases PurchasedQuantities(List<PurchaseLine> lines, Dictionary<int, Ingredient> ingredientsById)
    {
        var totals = new Dictionary<int, decimal>();
        var unconvertible = new HashSet<(DateTime Day, string Label)>();
        foreach (var line in lines)
        {
            if (!ingredientsById.TryGetValue(line.IngredientId, out var ingredient))
                continue;

            var price = ReceiptLinePricing.Compute(line.Quantity, line.Unit, line.LineTotalExclVat, ingredient);
            if (price.Issue == ReceiptLineIssue.InvalidTotal)
                price = ReceiptLinePricing.Compute(line.Quantity, line.Unit, lineTotalExclVat: 1m, ingredient);

            if (price.Issue == ReceiptLineIssue.None && price.BaseQuantity is { } quantity)
                totals[line.IngredientId] = totals.GetValueOrDefault(line.IngredientId) + quantity;
            else if (price.Issue == ReceiptLineIssue.IncompatibleUnit)
                unconvertible.Add((line.Day, line.Label));
        }

        var warnings = unconvertible
            .OrderBy(l => l.Day)
            .ThenBy(l => l.Label, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(l => l.Label, StringComparer.Ordinal)
            .Select(l => $"Ligne de ticket « {l.Label} » du {l.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} ignorée : unité non convertible")
            .ToList();

        return new Purchases(totals, warnings);
    }

    /// <summary>
    /// La consommation théorique d'ingrédients, en unité de base : la décomposition des plats vendus (une fois par plat, la décomposition étant linéaire)
    /// plus celle des pertes (un ingrédient jeté tel qu'acheté, sans rendement ; un plat ou une préparation décomposé comme une vente).
    /// Ce qui ne peut pas être décomposé est ignoré et signalé : une recette dont la décomposition remonte des problèmes (ligne inconnue, unité incompatible,
    /// quantité produite invalide, cycle) donne un avertissement, comme une perte d'ingrédient dont l'unité ne se convertit plus. Sans doublon.
    /// </summary>
    private static Consumption TheoreticalConsumption(
        Dataset data, List<Sale> sales, List<WasteEntry> waste, Dictionary<int, Ingredient> ingredientsById)
    {
        var explosion = new RecipeExplosion(data.Recipes, data.Ingredients);
        var gaps = new RecipeGaps(data.Recipes);
        var totals = new Dictionary<int, decimal>();
        var incompleteRecipeIds = new HashSet<int>();
        var emptyRecipeIds = new HashSet<int>();
        var unconvertibleWaste = new HashSet<(DateTime Day, int IngredientId)>();

        void Add(int ingredientId, decimal quantity) => totals[ingredientId] = totals.GetValueOrDefault(ingredientId) + quantity;

        void AddAll(int recipeId, ExplosionResult result)
        {
            if (result.Issues.Count > 0)
                incompleteRecipeIds.Add(recipeId);

            // Une recette sans ligne (ou une préparation sans ligne qu'elle utilise) se décompose en rien : sa consommation n'est pas nulle, elle est inconnue.
            emptyRecipeIds.UnionWith(gaps.EmptyRecipesUnder(recipeId));

            foreach (var (ingredientId, quantity) in result.GrossBaseQuantityByIngredient)
                Add(ingredientId, quantity);
        }

        // Les quantités nulles ou négatives sont déjà écartées au chargement : l'explosion ne les rejette pas.
        foreach (var group in sales.GroupBy(s => s.RecipeId))
            AddAll(group.Key, explosion.Explode(group.Key, group.Sum(s => s.Quantity)));

        foreach (var entry in waste.Where(e => e.Quantity > 0m))
        {
            if (entry.IngredientId is { } ingredientId)
            {
                if (!ingredientsById.TryGetValue(ingredientId, out var ingredient))
                    continue;

                if (WasteCosting.BaseQuantity(entry, ingredient) is { } quantity)
                    Add(ingredientId, quantity);
                else
                    unconvertibleWaste.Add((UtcDays.Of(entry.Date), ingredientId));
            }
            else if (entry.RecipeId is { } recipeId)
            {
                AddAll(recipeId, explosion.Explode(recipeId, entry.Quantity, entry.Unit));
            }
        }

        var recipeNames = data.Recipes.ToDictionary(r => r.Id, r => r.Name);
        var warnings = incompleteRecipeIds
            .Where(recipeNames.ContainsKey)
            .Select(id => recipeNames[id])
            .OrderBy(name => name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => $"Recette « {name} » incomplète : consommation théorique sous-estimée")
            .Concat(emptyRecipeIds
                .Where(recipeNames.ContainsKey)
                .Select(id => recipeNames[id])
                .OrderBy(name => name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .Select(name => $"Recette « {name} » sans ligne : consommation théorique inconnue"))
            .Concat(unconvertibleWaste
                .Select(w => (w.Day, ingredientsById[w.IngredientId].Name))
                .OrderBy(w => w.Day)
                .ThenBy(w => w.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(w => w.Name, StringComparer.Ordinal)
                .Select(w => $"Perte du {w.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} sur « {w.Name} » ignorée : unité non convertible"))
            .Distinct()
            .ToList();

        return new Consumption(totals, warnings);
    }

    // ── Aides ────────────────────────────────────────────────────────────────

    private static decimal Revenue(decimal quantity, decimal unitPriceInclVat, decimal vatRate) =>
        VatCalculator.ExclVat(quantity * unitPriceInclVat, vatRate);

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    /// <summary>Une vente (saisie ou ligne de commande) : son jour UTC, sa quantité de portions et son CA HT.</summary>
    private readonly record struct Sale(int RecipeId, DateTime Day, bool IsWeeklyTotal, bool FromOrder, decimal Quantity, decimal RevenueExclVat);

    /// <summary>La consommation théorique par ingrédient (unité de base) et les avertissements sur ce qu'elle sous-estime.</summary>
    private sealed record Consumption(Dictionary<int, decimal> Totals, IReadOnlyList<string> Warnings);

    /// <summary>Une ligne de ticket appliqué : son ingrédient, sa quantité et son total saisis, son libellé lu et le jour UTC du ticket.</summary>
    private readonly record struct PurchaseLine(
        int IngredientId, decimal? Quantity, MeasureUnit? Unit, decimal? LineTotalExclVat, string Label, DateTime Day);

    /// <summary>Les quantités achetées par ingrédient (unité de base) et les avertissements sur les lignes écartées.</summary>
    private sealed record Purchases(Dictionary<int, decimal> Totals, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Les recettes sans aucune ligne : leur coût et leur consommation sont inconnus, pas nuls. Une recette qui en utilise une, directement ou non, est dans le même cas.
    /// </summary>
    private sealed class RecipeGaps(IEnumerable<Recipe> recipes)
    {
        private readonly Dictionary<int, Recipe> _recipesById = recipes.ToDictionary(r => r.Id);
        private readonly Dictionary<int, bool> _hollow = [];

        /// <summary>Vrai si la recette n'a aucune ligne, ou utilise une préparation qui n'en a aucune.</summary>
        public bool IsHollow(int recipeId) =>
            _recipesById.TryGetValue(recipeId, out var recipe) && IsHollow(recipe, []);

        /// <summary>Les identifiants des recettes sans aucune ligne parmi <paramref name="recipeId"/> et les préparations qu'elle utilise, directement ou non.</summary>
        public HashSet<int> EmptyRecipesUnder(int recipeId)
        {
            var empty = new HashSet<int>();
            Collect(recipeId, new HashSet<int>(), empty);
            return empty;
        }

        private void Collect(int recipeId, HashSet<int> visited, HashSet<int> empty)
        {
            if (!visited.Add(recipeId) || !_recipesById.TryGetValue(recipeId, out var recipe))
                return;

            if (recipe.Lines.Count == 0)
                empty.Add(recipeId);

            foreach (var line in recipe.Lines)
            {
                if (line.SubRecipeId is { } subRecipeId)
                    Collect(subRecipeId, visited, empty);
            }
        }

        private bool IsHollow(Recipe recipe, HashSet<int> visiting)
        {
            if (_hollow.TryGetValue(recipe.Id, out var known))
                return known;

            if (recipe.Lines.Count == 0)
                return _hollow[recipe.Id] = true;

            // Une boucle de préparations est déjà signalée par le chiffrage : ici, elle ne rend pas la recette creuse.
            if (!visiting.Add(recipe.Id))
                return false;

            try
            {
                var hollow = recipe.Lines.Any(l => l.SubRecipeId is { } subRecipeId
                                                   && _recipesById.TryGetValue(subRecipeId, out var subRecipe)
                                                   && IsHollow(subRecipe, visiting));
                return _hollow[recipe.Id] = hollow;
            }
            finally
            {
                visiting.Remove(recipe.Id);
            }
        }
    }

    /// <summary>L'intervalle ]After, Through] entre les jours de deux inventaires clôturés, avec ces inventaires et leurs lignes.</summary>
    private sealed record VarianceWindow(DateTime After, DateTime Through, StockCount Opening, StockCount Closing);

    private sealed record Dataset(
        List<Ingredient> Ingredients,
        ILookup<int, IngredientPrice> History,
        List<Recipe> Recipes,
        List<Sale> Sales,
        List<WasteEntry> Waste,
        List<PurchaseLine> Purchases);
}
