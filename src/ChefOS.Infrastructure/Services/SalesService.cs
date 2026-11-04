using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Ventes réalisées hors de ChefOS (la caisse du chef), saisies dans une grille hebdomadaire : une quantité par plat, jour et service,
/// ou un total de semaine saisi d'un bloc (daté du lundi). Chaque saisie fige le prix de vente TTC et la TVA du plat au moment où elle est
/// enregistrée ; un changement de prix ultérieur ne la modifie pas.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un plat appartient bien au restaurant courant se fait ici,
/// via le contexte filtré par restaurant (qui estampille aussi les ajouts).
/// La semaine va du lundi au dimanche, selon le jour du calendrier UTC.
/// </summary>
public class SalesService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string UnknownDishMessage = "Plat introuvable";
    private const string NegativeQuantityMessage = "La quantité ne peut pas être négative";
    private const string WeeklyTotalMondayMessage = "Un total de semaine doit être daté du lundi";
    private const string QuantityTooHighMessage = "Quantité trop élevée";

    /// <summary>Plafond d'une saisie, en portions : au-delà, c'est une faute de frappe.</summary>
    private const decimal MaxQuantity = 1_000_000m;

    // Échelle de la colonne numeric : arrondir avant de valider évite qu'une quantité positive soit stockée comme 0.
    private const int QuantityScale = 4;

    /// <summary>
    /// La grille de la semaine qui contient <paramref name="anyDayUtc"/> : les plats non archivés, triés par catégorie (sans catégorie en dernier)
    /// puis par nom, et les plats archivés qui ont des ventes cette semaine-là. Chaque plat porte toutes ses saisies de la semaine :
    /// par jour et service, et le total hebdomadaire (daté du lundi), triés par date puis service.
    /// </summary>
    public async Task<SalesWeek> GetWeekAsync(DateTime anyDayUtc)
    {
        var monday = MondayOf(anyDayUtc);
        var nextMonday = monday.AddDays(7);

        await using var db = await factory.CreateDbContextAsync();

        var entries = await db.SalesEntries.AsNoTracking()
            .Where(e => e.Date >= monday && e.Date < nextMonday)
            .ToListAsync();
        var soldRecipeIds = entries.Select(e => e.RecipeId).Distinct().ToList();

        var dishes = await db.Recipes.AsNoTracking()
            .Where(r => r.Kind == RecipeKind.Dish && (!r.IsArchived || soldRecipeIds.Contains(r.Id)))
            .ToListAsync();

        var cellsByDish = entries
            .GroupBy(e => e.RecipeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SalesCell>)g
                    .OrderBy(e => e.Date)
                    .ThenBy(e => e.Service)
                    .ThenBy(e => e.IsWeeklyTotal)
                    .Select(e => new SalesCell(
                        e.RecipeId, DateTime.SpecifyKind(e.Date, DateTimeKind.Utc), e.Service, e.IsWeeklyTotal, e.Quantity))
                    .ToList());

        var rows = dishes
            .OrderBy(d => d.Category is null ? 1 : 0)
            .ThenBy(d => d.Category, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(d => d.Id)
            .Select(d => new SalesWeekDish(d.Id, d.Name, d.SellingPriceInclVat, cellsByDish.GetValueOrDefault(d.Id) ?? []))
            .ToList();

        return new SalesWeek(monday, rows);
    }

    /// <summary>
    /// Enregistre des cases de la grille, tout ou rien : toutes sont validées avant la moindre écriture, puis appliquées d'un seul
    /// <c>SaveChanges</c>. Par clé (plat, jour, service, hebdomadaire ou non), une quantité de 0 supprime la saisie ; une quantité positive crée
    /// la saisie en figeant le prix de vente TTC et la TVA actuels du plat, ou, si elle existe déjà, ne change que sa quantité : une saisie garde
    /// le prix figé à sa création (corriger les ventes d'une ancienne semaine ne réécrit pas son chiffre d'affaires), et une quantité inchangée
    /// ne touche à rien. Si une clé revient plusieurs fois, la dernière gagne.
    /// Les dates sont ramenées au jour UTC ; la quantité est arrondie à 4 décimales (échelle de la colonne). Un total de semaine n'a pas de service :
    /// il est enregistré en <see cref="ServicePeriod.Journee"/>, quel que soit celui de la case.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Quantité négative ou supérieure à 1 000 000, total de semaine qui n'est pas daté d'un lundi, plat inconnu (ou qui n'est pas un plat du
    /// restaurant courant), ou plat sans prix de vente alors qu'une quantité positive est saisie.
    /// </exception>
    public async Task SaveCellsAsync(IReadOnlyList<SalesCell> cells)
    {
        if (cells.Count == 0)
            return;

        var normalized = cells.Select(Normalize).ToList();

        await using var db = await factory.CreateDbContextAsync();

        var recipeIds = normalized.Select(c => c.RecipeId).Distinct().ToList();
        var dishes = await db.Recipes.AsNoTracking()
            .Where(r => r.Kind == RecipeKind.Dish && recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id);

        foreach (var cell in normalized)
        {
            var dish = dishes.GetValueOrDefault(cell.RecipeId)
                       ?? throw new DomainValidationException(UnknownDishMessage);
            if (cell.Quantity > 0 && (dish.SellingPriceInclVat is null || dish.VatRate is null))
                throw new DomainValidationException($"Le plat {dish.Name} n'a pas de prix de vente");
        }

        // Une même clé peut revenir : la dernière gagne.
        var effective = normalized
            .GroupBy(c => new CellKey(c.RecipeId, c.Date, c.Service, c.IsWeeklyTotal))
            .ToDictionary(g => g.Key, g => g.Last());

        var dates = effective.Keys.Select(k => k.Date).Distinct().ToList();
        var existing = (await db.SalesEntries
                .Where(e => recipeIds.Contains(e.RecipeId) && dates.Contains(e.Date))
                .ToListAsync())
            .ToDictionary(e => new CellKey(e.RecipeId, e.Date, e.Service, e.IsWeeklyTotal));

        var now = DateTime.UtcNow;
        foreach (var (key, cell) in effective)
        {
            existing.TryGetValue(key, out var entry);

            if (cell.Quantity == 0)
            {
                if (entry is not null)
                    db.SalesEntries.Remove(entry);
                continue;
            }

            if (entry is null)
            {
                // Seule une nouvelle saisie fige le prix du plat ; une saisie existante garde celui de sa création.
                var dish = dishes[cell.RecipeId];
                db.SalesEntries.Add(new SalesEntry
                {
                    RecipeId = key.RecipeId,
                    Date = key.Date,
                    Service = key.Service,
                    IsWeeklyTotal = key.IsWeeklyTotal,
                    Quantity = cell.Quantity,
                    UnitPriceInclVat = dish.SellingPriceInclVat!.Value,
                    VatRate = dish.VatRate!.Value,
                    UpdatedAt = now
                });
            }
            else if (entry.Quantity != cell.Quantity)
            {
                entry.Quantity = cell.Quantity;
                entry.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync();
    }

    private readonly record struct CellKey(int RecipeId, DateTime Date, ServicePeriod Service, bool IsWeeklyTotal);

    /// <summary>La case avec sa date ramenée au jour UTC et sa quantité arrondie ; refuse une quantité négative ou trop élevée et un total de semaine hors lundi.</summary>
    private static SalesCell Normalize(SalesCell cell)
    {
        var quantity = Math.Round(cell.Quantity, QuantityScale, MidpointRounding.AwayFromZero);
        if (quantity < 0)
            throw new DomainValidationException(NegativeQuantityMessage);

        if (quantity > MaxQuantity)
            throw new DomainValidationException(QuantityTooHighMessage);

        var date = UtcDay(cell.Date);
        if (cell.IsWeeklyTotal && date.DayOfWeek != DayOfWeek.Monday)
            throw new DomainValidationException(WeeklyTotalMondayMessage);

        // Un total de semaine couvre tous les services : une seule clé, en « Journée ».
        var service = cell.IsWeeklyTotal ? ServicePeriod.Journee : cell.Service;
        return cell with { Date = date, Service = service, Quantity = quantity };
    }

    /// <summary>Minuit UTC du jour de <paramref name="value"/> ; une date sans fuseau est lue comme étant en UTC.</summary>
    private static DateTime UtcDay(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
    }

    /// <summary>Le lundi (00:00 UTC) de la semaine qui contient <paramref name="value"/> ; la semaine commence le lundi.</summary>
    private static DateTime MondayOf(DateTime value)
    {
        var day = UtcDay(value);
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-sinceMonday);
    }
}
