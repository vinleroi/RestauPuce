using System.Globalization;
using System.Text;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Seeding;

/// <summary>
/// L'activité de « Le Petit Bistro » : de quoi donner vie aux ventes, aux pertes et aux statistiques dès le premier lancement.
/// <list type="bullet">
/// <item><description>8 semaines de ventes saisies au jour (le bistro est fermé le lundi), au prix de vente figé de chaque plat ;</description></item>
/// <item><description>une quinzaine de commandes sur les 7 derniers jours, dont une annulée ;</description></item>
/// <item><description>6 pertes (ingrédients, préparation, plat) ;</description></item>
/// <item><description>des tickets de fournisseurs (dont Metro) appliqués, et deux inventaires clôturés, à J-28 et à J (le jour même), qui les encadrent.</description></item>
/// </list>
/// Les volumes sont choisis pour que les quatre quadrants du menu engineering soient peuplés sur les 4 dernières semaines, les marges venant des prix et des coûts
/// du seed. Les inventaires sont <b>calculés</b> à partir des ventes et des pertes semées (consommation théorique, comme le fait <see cref="Services.StatsService"/>) :
/// la consommation réelle ne diffère que de ±0,5 % de la théorique, sauf pour trois produits chers (beurre, crème, crevettes grises : +9 à +14 %), qui font apparaître un écart positif.
/// Le second inventaire est daté du jour même : un écart n'existe que pour une période qui finit à la date d'un inventaire clôturé, ou avant lui.
/// Tout est relatif à <c>now</c> (jours UTC), déterministe (aucun hasard) et écrit directement dans le contexte, avec les règles des services : prix figés,
/// commandes numérotées de 1 à n, tickets appliqués avec le prix de chaque ligne.
/// </summary>
internal static class DemoActivity
{
    private const string MetroName = "Metro";
    private const string MetroTicketName = "Metro Cash & Carry";

    private const decimal FoodVat = 0.06m;
    private const decimal DrinkVat = 0.21m;

    /// <summary>Stock laissé à la clôture, en part de la consommation des 28 jours (une semaine de stock) ; celui d'ouverture lui ressemble.</summary>
    private const decimal ClosingStockShare = 0.25m;

    /// <summary>Les herbes ne sont pas comptées : elles figurent dans « Non comptés aux deux inventaires ».</summary>
    private static readonly HashSet<string> NeverCounted = ["Persil plat", "Thym frais", "Laurier séché"];

    /// <summary>Part de pertes inexpliquées (sur-portionnement, vol, pertes non déclarées) de quelques produits chers ; le reste varie de -0,5 % à +0,5 %.</summary>
    private static readonly Dictionary<string, decimal> UnexplainedLoss = new()
    {
        ["Beurre doux"] = 0.13m,
        ["Crème fraîche liquide 35 %"] = 0.14m,
        ["Crevettes grises décortiquées"] = 0.09m
    };

    /// <summary>Portions vendues par semaine de référence, par plat : choisies pour que les quatre quadrants du menu engineering soient peuplés.</summary>
    private static readonly (string Dish, int PerWeek)[] WeeklyPortions =
    [
        ("Moules-frites", 100),            // populaire, marge élevée : étoile
        ("Carbonnade flamande", 80),       // étoile
        ("Waterzooi de poulet", 58),       // étoile
        ("Croquettes de crevettes", 64),   // populaire mais food cost de 37 % : vache à lait
        ("Boulets liégeois", 46),          // vache à lait
        ("Américain préparé", 38),         // vache à lait
        ("Lapin à la bière", 20),          // cher et peu commandé : énigme
        ("Cabillaud beurre blanc", 16),    // énigme
        ("Vol-au-vent", 18),               // poids mort
        ("Gaufre de Bruxelles", 17),       // poids mort
        ("Chicons au gratin", 14),         // poids mort
        ("Salade liégeoise", 11)           // poids mort
    ];

    /// <summary>Poids de chaque jour dans la semaine (lundi fermé) ; la somme vaut 1.</summary>
    private static readonly decimal[] WeekdayShare = BuildWeekdayShares();

    private static decimal[] BuildWeekdayShares()
    {
        var shares = new decimal[7];
        shares[(int)DayOfWeek.Tuesday] = 0.11m;
        shares[(int)DayOfWeek.Wednesday] = 0.13m;
        shares[(int)DayOfWeek.Thursday] = 0.15m;
        shares[(int)DayOfWeek.Friday] = 0.22m;
        shares[(int)DayOfWeek.Saturday] = 0.25m;
        shares[(int)DayOfWeek.Sunday] = 0.14m;
        return shares;
    }

    public static async Task AddAsync(ChefOsDbContext db, DateTime now, CancellationToken ct)
    {
        var today = Utc(now.Date);

        var recipes = await db.Recipes.Include(r => r.Lines).ToListAsync(ct);
        var dishes = recipes.Where(r => r.Kind == RecipeKind.Dish).ToDictionary(r => r.Name);
        var preparations = recipes.Where(r => r.Kind == RecipeKind.Preparation).ToDictionary(r => r.Name);
        var ingredients = await db.Ingredients.ToListAsync(ct);
        var ingredientsByName = ingredients.ToDictionary(i => i.Name);
        var suppliersById = (await db.Suppliers.ToListAsync(ct)).ToDictionary(s => s.Id);
        var history = (await db.IngredientPrices.ToListAsync(ct)).ToLookup(p => p.IngredientId);

        var metro = new Supplier
        {
            Name = MetroName,
            ContactName = "Service clients professionnels",
            Email = "bruxelles@metro.example",
            Phone = "+32 2 555 01 40"
        };
        db.Suppliers.Add(metro);
        // L'identifiant de Metro est nécessaire aux tickets et aux prix (sans propriété de navigation).
        await db.SaveChangesAsync(ct);

        var entries = SalesEntries(dishes, today);
        var orders = Orders(dishes, today, now);
        var waste = WasteEntries(dishes, preparations, ingredientsByName, today);
        db.SalesEntries.AddRange(entries);
        db.Orders.AddRange(orders);
        db.WasteEntries.AddRange(waste);

        // Ce que les statistiques compteront comme vendu sur les 28 jours qui finissent aujourd'hui : les saisies et les commandes non annulées.
        var openingDay = today.AddDays(-28);
        var sold = entries.Where(e => e.Date > openingDay)
            .Select(e => (e.RecipeId, e.Quantity))
            .Concat(orders.Where(o => o.Status != OrderStatus.Annulee && UtcDay(o.CreatedAt) > openingDay)
                .SelectMany(o => o.Lines).Select(l => (l.RecipeId, (decimal)l.Quantity)))
            .ToList();

        var theoretical = TheoreticalConsumption(recipes, ingredients, sold, waste.Where(w => w.Date > openingDay).ToList());
        AddReceiptsAndCounts(db, today, now, metro, suppliersById, ingredients, history, theoretical);

        await db.SaveChangesAsync(ct);
    }

    // ── Ventes saisies ───────────────────────────────────────────────────────

    /// <summary>
    /// Une saisie « Journée » par plat et par jour d'ouverture, de J-56 à J-1. Le volume suit le plat, le jour de la semaine, une tendance à la hausse
    /// (+1,5 point de plus chaque semaine) et un léger aléa déterministe (±12 %).
    /// </summary>
    private static List<SalesEntry> SalesEntries(IReadOnlyDictionary<string, Recipe> dishes, DateTime today)
    {
        var entries = new List<SalesEntry>();

        for (var daysAgo = 56; daysAgo >= 1; daysAgo--)
        {
            var day = today.AddDays(-daysAgo);
            var share = WeekdayShare[(int)day.DayOfWeek];
            if (share == 0m)
                continue;

            // Semaine 0 : les 7 derniers jours ; semaine 7 : les plus anciens.
            var week = (daysAgo - 1) / 7;
            var trend = 1.04m - 0.015m * week;

            for (var index = 0; index < WeeklyPortions.Length; index++)
            {
                var (name, perWeek) = WeeklyPortions[index];
                var dish = dishes[name];
                var jitter = 1m + ((index * 7 + (int)(day - DateTime.UnixEpoch).TotalDays * 13) % 9 - 4) * 0.03m;
                var quantity = Math.Round(perWeek * trend * share * jitter, 0, MidpointRounding.AwayFromZero);
                if (quantity <= 0m)
                    continue;

                entries.Add(new SalesEntry
                {
                    RecipeId = dish.Id,
                    Date = day,
                    Service = ServicePeriod.Journee,
                    IsWeeklyTotal = false,
                    Quantity = quantity,
                    UnitPriceInclVat = dish.SellingPriceInclVat!.Value,
                    VatRate = dish.VatRate!.Value,
                    UpdatedAt = day.AddHours(23)
                });
            }
        }

        return entries;
    }

    // ── Commandes ────────────────────────────────────────────────────────────

    private sealed record OrderScript(DateTime CreatedAt, OrderSource Source, string? Who, OrderStatus Status, (string Dish, int Quantity)[] Lines);

    /// <summary>
    /// Seize commandes sur les 7 derniers jours, numérotées de 1 à n dans l'ordre du temps. Les plus anciennes sont servies (sauf une, annulée),
    /// celles d'aujourd'hui montrent le tableau en cours de service. Le prix et la TVA de chaque ligne sont ceux du plat.
    /// </summary>
    private static List<Order> Orders(IReadOnlyDictionary<string, Recipe> dishes, DateTime today, DateTime now)
    {
        DateTime Past(int daysAgo, int hour, int minute) => today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);
        DateTime Ago(int minutes) => Utc(now.AddMinutes(-minutes));

        OrderScript[] scripts =
        [
            new(Past(6, 12, 35), OrderSource.Salle, "Table 4", OrderStatus.Servie, [("Carbonnade flamande", 2), ("Moules-frites", 1)]),
            new(Past(6, 19, 50), OrderSource.Emporter, "M. Dubois", OrderStatus.Servie, [("Boulets liégeois", 1), ("Waterzooi de poulet", 1), ("Gaufre de Bruxelles", 2)]),
            new(Past(5, 13, 10), OrderSource.Telephone, "Mme Peeters", OrderStatus.Servie, [("Croquettes de crevettes", 2), ("Cabillaud beurre blanc", 1)]),
            new(Past(5, 20, 15), OrderSource.Salle, "Table 7", OrderStatus.Servie, [("Moules-frites", 3), ("Américain préparé", 1)]),
            new(Past(4, 12, 20), OrderSource.Vitrine, "Commande en ligne", OrderStatus.Servie, [("Lapin à la bière", 1), ("Salade liégeoise", 1)]),
            new(Past(4, 19, 30), OrderSource.Salle, "Table 2", OrderStatus.Servie, [("Carbonnade flamande", 2), ("Waterzooi de poulet", 2), ("Gaufre de Bruxelles", 2)]),
            new(Past(3, 12, 55), OrderSource.Emporter, "Mme Janssens", OrderStatus.Annulee, [("Vol-au-vent", 2), ("Boulets liégeois", 1)]),
            new(Past(3, 20, 5), OrderSource.Salle, "Table 5", OrderStatus.Servie, [("Moules-frites", 2), ("Croquettes de crevettes", 1)]),
            new(Past(2, 12, 45), OrderSource.Salle, "Table 1", OrderStatus.Servie, [("Chicons au gratin", 1), ("Waterzooi de poulet", 1), ("Carbonnade flamande", 1)]),
            new(Past(2, 19, 40), OrderSource.Telephone, "Famille Claes", OrderStatus.Servie, [("Moules-frites", 4), ("Gaufre de Bruxelles", 3)]),
            new(Past(1, 13, 0), OrderSource.Vitrine, "Commande en ligne", OrderStatus.Servie, [("Boulets liégeois", 2), ("Américain préparé", 1)]),
            new(Past(1, 20, 10), OrderSource.Salle, "Table 6", OrderStatus.Servie, [("Cabillaud beurre blanc", 2), ("Lapin à la bière", 1), ("Croquettes de crevettes", 2)]),
            new(Ago(150), OrderSource.Salle, "Table 3", OrderStatus.Servie, [("Carbonnade flamande", 2), ("Moules-frites", 2)]),
            new(Ago(40), OrderSource.Emporter, "M. Lambert", OrderStatus.Prete, [("Boulets liégeois", 2), ("Waterzooi de poulet", 1)]),
            new(Ago(18), OrderSource.Salle, "Table 8", OrderStatus.EnPreparation, [("Moules-frites", 2), ("Croquettes de crevettes", 2), ("Gaufre de Bruxelles", 2)]),
            new(Ago(4), OrderSource.Telephone, "Mme Dubois", OrderStatus.Nouvelle, [("Américain préparé", 1), ("Salade liégeoise", 1)])
        ];

        return scripts
            .OrderBy(s => s.CreatedAt)
            .Select((script, index) => new Order
            {
                Number = index + 1,
                CreatedAt = script.CreatedAt,
                Source = script.Source,
                Status = script.Status,
                TableOrCustomer = script.Who,
                StatusChangedAt = script.CreatedAt.AddMinutes(StatusDelayMinutes(script.Status)),
                Lines = script.Lines
                    .Select(l => new OrderLine
                    {
                        RecipeId = dishes[l.Dish].Id,
                        Quantity = l.Quantity,
                        UnitPriceInclVat = dishes[l.Dish].SellingPriceInclVat!.Value,
                        VatRate = dishes[l.Dish].VatRate!.Value
                    })
                    .ToList()
            })
            .ToList();
    }

    /// <summary>Le temps écoulé entre la prise de la commande et son statut actuel (jamais dans le futur : les commandes en cours datent de 40, 18 et 4 minutes).</summary>
    private static int StatusDelayMinutes(OrderStatus status) => status switch
    {
        OrderStatus.Nouvelle => 0,
        OrderStatus.EnPreparation => 8,
        OrderStatus.Prete => 25,
        OrderStatus.Servie => 45,
        OrderStatus.Annulee => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    // ── Pertes ───────────────────────────────────────────────────────────────

    private static List<WasteEntry> WasteEntries(
        IReadOnlyDictionary<string, Recipe> dishes, IReadOnlyDictionary<string, Recipe> preparations,
        IReadOnlyDictionary<string, Ingredient> ingredients, DateTime today)
    {
        WasteEntry Entry(int daysAgo, int? ingredientId, int? recipeId, decimal quantity, MeasureUnit unit, WasteReason reason, string note) => new()
        {
            Date = today.AddDays(-daysAgo),
            IngredientId = ingredientId,
            RecipeId = recipeId,
            Quantity = quantity,
            Unit = unit,
            Reason = reason,
            Note = note,
            CreatedAt = today.AddDays(-daysAgo).AddHours(17).AddMinutes(30)
        };

        return
        [
            Entry(1, ingredients["Salade verte"].Id, null, 0.8m, MeasureUnit.Kg, WasteReason.Perime, "Fanée en fin de semaine"),
            Entry(3, ingredients["Crevettes grises décortiquées"].Id, null, 0.25m, MeasureUnit.Kg, WasteReason.Perime, "Date limite dépassée"),
            Entry(6, null, dishes["Américain préparé"].Id, 2m, MeasureUnit.Piece, WasteReason.Erreur, "Erreur de commande en salle"),
            Entry(9, ingredients["Bière brune d'abbaye"].Id, null, 1.5m, MeasureUnit.L, WasteReason.Casse, "Caisse tombée à la livraison"),
            Entry(12, null, preparations["Sauce lapin à la bière"].Id, 2m, MeasureUnit.L, WasteReason.Surproduction, "Lot trop important pour la semaine"),
            Entry(16, ingredients["Beurre doux"].Id, null, 0.5m, MeasureUnit.Kg, WasteReason.Autre, "Oublié hors du frigo après la livraison")
        ];
    }

    // ── Consommation théorique ───────────────────────────────────────────────

    /// <summary>
    /// La consommation théorique en unité de base, calculée comme le fait <see cref="Services.StatsService"/> : la décomposition des plats vendus
    /// (une fois par plat) plus celle des pertes (un ingrédient tel quel, un plat ou une préparation décomposé).
    /// </summary>
    private static Dictionary<int, decimal> TheoreticalConsumption(
        IReadOnlyList<Recipe> recipes, IReadOnlyList<Ingredient> ingredients, IReadOnlyList<(int RecipeId, decimal Quantity)> sold,
        IReadOnlyList<WasteEntry> waste)
    {
        var explosion = new RecipeExplosion(recipes, ingredients);
        var ingredientsById = ingredients.ToDictionary(i => i.Id);
        var totals = new Dictionary<int, decimal>();

        void Add(int ingredientId, decimal quantity) => totals[ingredientId] = totals.GetValueOrDefault(ingredientId) + quantity;

        void AddAll(ExplosionResult result)
        {
            foreach (var (ingredientId, quantity) in result.GrossBaseQuantityByIngredient)
                Add(ingredientId, quantity);
        }

        foreach (var group in sold.GroupBy(s => s.RecipeId))
            AddAll(explosion.Explode(group.Key, group.Sum(s => s.Quantity)));

        foreach (var entry in waste)
        {
            if (entry.IngredientId is { } ingredientId)
            {
                var ingredient = ingredientsById[ingredientId];
                var converted = UnitConverter.ToBase(entry.Quantity, entry.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
                Add(ingredientId, converted.Value);
            }
            else
            {
                AddAll(explosion.Explode(entry.RecipeId!.Value, entry.Quantity, entry.Unit));
            }
        }

        return totals;
    }

    // ── Tickets appliqués et inventaires ─────────────────────────────────────

    /// <summary>Les jours (avant aujourd'hui) où chaque fournisseur livre, et donc où son ticket est daté.</summary>
    private static readonly int[] DeliveryDaysAgo = [22, 11];

    private sealed record Delivery(Supplier Supplier, int DaysAgo, string Prefix, List<(Ingredient Ingredient, decimal Quantity)> Lines);

    /// <summary>
    /// Pour chaque ingrédient consommé et compté : une consommation réelle (théorique ± écart), des achats qui la couvrent (à l'arrondi des cartons près), répartis
    /// sur les livraisons du fournisseur, un stock d'ouverture et un stock de clôture qui bouclent le calcul (ouverture + achats - clôture = consommation réelle),
    /// à la précision d'un comptage (50 g, 50 ml, une pièce) près. Les deux inventaires comptent les mêmes ingrédients.
    /// </summary>
    private static void AddReceiptsAndCounts(
        ChefOsDbContext db, DateTime today, DateTime now, Supplier metro, IReadOnlyDictionary<int, Supplier> suppliersById,
        IReadOnlyList<Ingredient> ingredients, ILookup<int, IngredientPrice> history, IReadOnlyDictionary<int, decimal> theoretical)
    {
        var deliveries = new Dictionary<(int SupplierId, int DaysAgo), Delivery>();
        var opening = new List<StockCountLine>();
        var closing = new List<StockCountLine>();

        var counted = ingredients
            .Where(i => !NeverCounted.Contains(i.Name) && theoretical.GetValueOrDefault(i.Id) > 0m)
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        for (var index = 0; index < counted.Count; index++)
        {
            var ingredient = counted[index];
            var unit = DisplayUnit(ingredient.BaseUnit);

            var loss = UnexplainedLoss.TryGetValue(ingredient.Name, out var unexplained) ? unexplained : -0.005m + 0.00125m * (index * 7 % 9);
            var actualUse = theoretical[ingredient.Id] * (1m + loss);

            // Achats : les livraisons du fournisseur se partagent la consommation ; un petit besoin tient en une seule livraison.
            var buyer = BuyingSupplier(ingredient, metro, suppliersById);
            var target = ToDisplay(actualUse, ingredient);
            var perDelivery = target / DeliveryDaysAgo.Length;
            var days = perDelivery < (unit == MeasureUnit.Piece ? 6m : 3m) ? DeliveryDaysAgo[..1] : DeliveryDaysAgo;
            decimal purchasedBase = 0m;
            foreach (var daysAgo in days)
            {
                var quantity = NiceQuantity(target / days.Length, unit);
                if (!deliveries.TryGetValue((buyer.Id, daysAgo), out var delivery))
                {
                    delivery = new Delivery(buyer, daysAgo, TicketPrefix(buyer, metro), []);
                    deliveries[(buyer.Id, daysAgo)] = delivery;
                }

                delivery.Lines.Add((ingredient, quantity));
                purchasedBase += ToBase(quantity, unit, ingredient);
            }

            // Stocks : celui de clôture vaut un quart de la consommation, celui d'ouverture ferme le calcul (au moins un dixième de la consommation).
            // Les achats arrondis aux cartons diffèrent un peu de la consommation : c'est le stock d'ouverture qui absorbe la différence.
            var openingTarget = Math.Max(ClosingStockShare * actualUse + actualUse - purchasedBase, 0.1m * actualUse);
            var openingCount = RoundCount(ToDisplay(openingTarget, ingredient), unit);
            var closingCount = Math.Max(RoundCount(ToDisplay(ToBase(openingCount, unit, ingredient) + purchasedBase - actualUse, ingredient), unit), 0m);

            opening.Add(CountLine(ingredient, openingCount, unit));
            closing.Add(CountLine(ingredient, closingCount, unit));
        }

        foreach (var delivery in deliveries.Values.OrderByDescending(d => d.DaysAgo).ThenBy(d => d.Supplier.Name, StringComparer.Ordinal))
            AddReceipt(db, today, delivery, history);

        db.StockCounts.Add(new StockCount
        {
            Date = today.AddDays(-28),
            Status = StockCountStatus.Cloture,
            Note = "Inventaire de réserve",
            ClosedAt = today.AddDays(-28).AddHours(22).AddMinutes(30),
            Lines = opening
        });
        db.StockCounts.Add(new StockCount
        {
            Date = today,
            Status = StockCountStatus.Cloture,
            Note = "Inventaire de réserve",
            // Compté ce matin, ou à l'instant si le démarrage a lieu avant ; jamais dans le futur.
            ClosedAt = Utc(new[] { today.AddHours(6).AddMinutes(30), now.AddMinutes(-1) }.Min()),
            Lines = closing
        });
    }

    private static void AddReceipt(ChefOsDbContext db, DateTime today, Delivery delivery, ILookup<int, IngredientPrice> history)
    {
        var day = today.AddDays(-delivery.DaysAgo);
        var receipt = new Receipt
        {
            SupplierId = delivery.Supplier.Id,
            SupplierNameRaw = delivery.Supplier.Name == MetroName ? MetroTicketName : delivery.Supplier.Name,
            ReceiptDate = day,
            InvoiceNumber = $"{delivery.Prefix}-{day:yyMMdd}",
            AmountsIncludeVat = false,
            Status = ReceiptStatus.Applied,
            CreatedAt = day.AddHours(9).AddMinutes(15),
            AppliedAt = day.AddHours(9).AddMinutes(40)
        };

        var position = 0;
        foreach (var (ingredient, quantity) in delivery.Lines.OrderBy(l => l.Ingredient.Name, StringComparer.Ordinal))
        {
            var unit = DisplayUnit(ingredient.BaseUnit);
            // Le prix du ticket est celui qui était en vigueur ce jour-là : ni hausse ni baisse inventée.
            var priceAtDay = PriceAsOf.At(history[ingredient.Id], day, ingredient.CurrentPricePerBaseUnit)!.Value;
            var unitPrice = Math.Round(UnitConverter.PriceDisplayFromBase(priceAtDay, ingredient.BaseUnit), 2, MidpointRounding.AwayFromZero);
            var lineTotal = Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
            var appliedPrice = Math.Round(lineTotal / ToBase(quantity, unit, ingredient), 6, MidpointRounding.AwayFromZero);

            receipt.Lines.Add(new ReceiptLine
            {
                Position = position++,
                RawLabel = TicketLabel(ingredient.Name, quantity, unit),
                Quantity = quantity,
                Unit = unit,
                UnitPrice = unitPrice,
                LineTotal = lineTotal,
                VatRate = ingredient.Category == "Boissons" ? DrinkVat : FoodVat,
                LineTotalExclVat = lineTotal,
                VatToCheck = false,
                IngredientId = ingredient.Id,
                MatchSource = MatchSource.NameMatch,
                IsIgnored = false,
                AppliedPricePerBaseUnit = appliedPrice
            });

            // Comme à l'application d'un ticket : le prix entre dans l'historique, au jour du ticket.
            db.IngredientPrices.Add(new IngredientPrice
            {
                IngredientId = ingredient.Id,
                Date = day,
                PricePerBaseUnit = appliedPrice,
                SupplierId = delivery.Supplier.Id,
                Source = PriceSource.Receipt
            });
        }

        receipt.Total = receipt.Lines.Sum(l => l.LineTotal);
        db.Receipts.Add(receipt);
    }

    /// <summary>Metro livre l'épicerie, les produits laitiers, les boissons et les légumes de garde ; le reste vient du fournisseur habituel de l'ingrédient.</summary>
    private static Supplier BuyingSupplier(Ingredient ingredient, Supplier metro, IReadOnlyDictionary<int, Supplier> suppliersById)
    {
        var atMetro = ingredient.Category is "Épicerie" or "Boissons" or "Produits laitiers"
                      || ingredient.Name is "Pommes de terre Bintje" or "Oignons jaunes" or "Carottes";
        return atMetro || ingredient.SupplierId is not { } supplierId ? metro : suppliersById[supplierId];
    }

    private static string TicketPrefix(Supplier supplier, Supplier metro) =>
        supplier.Id == metro.Id ? "MET" : new string(supplier.Name.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();

    /// <summary>« BEURRE DOUX 5KG » : le libellé tel qu'un ticket l'imprime, en majuscules et sans accents.</summary>
    private static string TicketLabel(string name, decimal quantity, MeasureUnit unit)
    {
        var plain = new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        var suffix = unit switch
        {
            MeasureUnit.Kg => "KG",
            MeasureUnit.L => "L",
            _ => "PCE"
        };
        return $"{plain.ToUpperInvariant()} {quantity.ToString("0.##", CultureInfo.InvariantCulture)}{suffix}";
    }

    // ── Unités et arrondis ───────────────────────────────────────────────────

    private static MeasureUnit DisplayUnit(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => MeasureUnit.Kg,
        BaseUnit.Milliliter => MeasureUnit.L,
        _ => MeasureUnit.Piece
    };

    /// <summary>Une quantité en unité de base, exprimée dans l'unité des tickets et des comptages (kg, l, pièce).</summary>
    private static decimal ToDisplay(decimal baseQuantity, Ingredient ingredient) =>
        ingredient.BaseUnit == BaseUnit.Piece ? baseQuantity : baseQuantity / 1000m;

    private static decimal ToBase(decimal quantity, MeasureUnit unit, Ingredient ingredient) =>
        UnitConverter.ToBase(quantity, unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams).Value;

    /// <summary>Une quantité de livraison qu'on commande vraiment : des cartons de 6 ou 30 pièces, des kilos ronds, jamais moins d'une unité.</summary>
    private static decimal NiceQuantity(decimal quantity, MeasureUnit unit)
    {
        var step = unit == MeasureUnit.Piece
            ? quantity >= 120m ? 30m : quantity >= 24m ? 6m : 1m
            : quantity >= 100m ? 10m : quantity >= 30m ? 5m : 1m;
        return Math.Max(step, Math.Round(quantity / step, 0, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>La précision d'un comptage : à la pièce, ou à 50 g / 50 ml près.</summary>
    private static decimal RoundCount(decimal quantity, MeasureUnit unit) =>
        unit == MeasureUnit.Piece
            ? Math.Round(quantity, 0, MidpointRounding.AwayFromZero)
            : Math.Round(quantity / 0.05m, 0, MidpointRounding.AwayFromZero) * 0.05m;

    private static StockCountLine CountLine(Ingredient ingredient, decimal quantity, MeasureUnit unit) => new()
    {
        IngredientId = ingredient.Id,
        Quantity = quantity,
        Unit = unit,
        BaseQuantity = ToBase(quantity, unit, ingredient)
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime UtcDay(DateTime value) => Utc(value.Date);
}
