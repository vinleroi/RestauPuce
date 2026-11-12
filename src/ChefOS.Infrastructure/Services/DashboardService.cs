using ChefOS.Core.Costing;
using ChefOS.Core.Recipes;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Chiffres clés du restaurant courant. Seuls les plats et préparations non archivés comptent : un élément archivé est
/// retiré de la carte et ne doit ni peser sur la moyenne ni apparaître dans les plats à surveiller.
/// Les chiffres de la semaine viennent de <see cref="StatsService"/> : ce sont les mêmes que ceux de la page des statistiques.
/// </summary>
public class DashboardService(RecipeService recipes, StatsService stats, IDbContextFactory<ChefOsDbContext> factory, ITenantContext tenant, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Nombre de jours des chiffres de la semaine, aujourd'hui compris.</summary>
    private const int WeekDays = 7;

    /// <summary>
    /// La moyenne du food cost porte sur les plats au coût complet et au food cost non nul (un plat sans ligne ou sans prix de vente n'a pas de food cost significatif).
    /// La liste « à surveiller » réunit les plats en <see cref="FoodCostLevel.Alert"/> (au-dessus du seuil du restaurant)
    /// ou <see cref="FoodCostLevel.Incomplete"/>, ces derniers d'abord, puis par food cost décroissant.
    /// </summary>
    public async Task<DashboardSummary> GetSummaryAsync()
    {
        var restaurantId = tenant.CurrentRestaurantId
            ?? throw new InvalidOperationException("Aucun restaurant courant.");
        var threshold = await GetAlertThresholdAsync(restaurantId);

        var book = await recipes.LoadBookAsync();
        var active = book.Recipes.Where(r => !r.IsArchived).ToList();
        var dishes = active
            .Where(r => r.Kind == RecipeKind.Dish)
            .Select(r => (Recipe: r, Cost: book.Cost(r.Id)))
            .ToList();

        var foodCosts = dishes
            .Where(d => d.Cost is { IsComplete: true, FoodCostPercent: > 0m })
            .Select(d => d.Cost.FoodCostPercent!.Value)
            .ToList();

        var watchList = dishes
            .Select(d => (d.Recipe, Level: FoodCostBadge.Classify(d.Cost, threshold), d.Cost))
            .Where(d => d.Level is FoodCostLevel.Alert or FoodCostLevel.Incomplete)
            // Un coût incomplet n'a pas de pourcentage fiable : il ne porterait que sur les lignes chiffrées.
            .Select(d => new WatchItem(
                d.Recipe.Id, d.Recipe.Name, d.Level == FoodCostLevel.Alert ? d.Cost.FoodCostPercent : null, d.Level))
            .OrderBy(w => w.Level == FoodCostLevel.Incomplete ? 0 : 1)
            .ThenByDescending(w => w.FoodCostPercent)
            .ThenBy(w => w.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

        return new DashboardSummary(
            dishes.Count,
            active.Count(r => r.Kind == RecipeKind.Preparation),
            foodCosts.Count == 0 ? null : foodCosts.Average(),
            watchList);
    }

    /// <summary>
    /// Chiffre d'affaires HT et marge brute des 7 derniers jours, aujourd'hui compris (de J-6 à J, jours du restaurant), ventes saisies et commandes additionnées
    /// (voir <see cref="StatsService.GetAsync"/> pour les règles de calcul).
    /// </summary>
    public async Task<SevenDayFigures> GetLastSevenDaysAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var to = (await RestaurantCalendar.ForCurrentRestaurantAsync(db, _clock)).Today;
        var from = to.AddDays(1 - WeekDays);

        var period = await stats.GetAsync(from, to);
        return new SevenDayFigures(from, to, period.RevenueExclVat, period.GrossMargin, period.CostIncomplete);
    }

    /// <summary>
    /// Variations de prix récentes des ingrédients non archivés du restaurant courant. Pour chaque ingrédient, on compare ses deux
    /// dernières entrées d'historique (date décroissante, puis identifiant décroissant en cas d'égalité) : la dernière doit dater d'au plus
    /// <paramref name="days"/> jours et la variation, en valeur absolue, atteindre <paramref name="minChange"/> (une fraction : 0,05 = 5 %).
    /// Un ingrédient à moins de deux entrées, ou dont le prix précédent est nul, est ignoré. Hausses d'abord (la plus forte en tête),
    /// puis baisses (la plus forte en dernier) ; à variation égale, par nom.
    /// </summary>
    public async Task<IReadOnlyList<PriceChange>> GetRecentPriceChangesAsync(int days = 30, decimal minChange = 0.05m)
    {
        var since = _clock.GetUtcNow().UtcDateTime.AddDays(-days);

        await using var db = await factory.CreateDbContextAsync();

        // La dernière entrée est la plus récente : « une entrée à partir de `since` » équivaut à « dernière entrée récente ».
        var candidates = await db.Ingredients
            .AsNoTracking()
            .Where(i => !i.IsArchived && db.IngredientPrices.Any(p => p.IngredientId == i.Id && p.Date >= since))
            .Select(i => new
            {
                i.Id,
                i.Name,
                i.BaseUnit,
                LatestTwo = db.IngredientPrices
                    .Where(p => p.IngredientId == i.Id)
                    .OrderByDescending(p => p.Date)
                    .ThenByDescending(p => p.Id)
                    .Take(2)
                    .Select(p => new { p.Date, p.PricePerBaseUnit })
                    .ToList()
            })
            .ToListAsync();

        return candidates
            .Where(c => c.LatestTwo.Count == 2 && c.LatestTwo[1].PricePerBaseUnit > 0m)
            .Select(c =>
            {
                var current = c.LatestTwo[0];
                var previous = c.LatestTwo[1].PricePerBaseUnit;

                return new PriceChange(
                    c.Id, c.Name, c.BaseUnit, previous, current.PricePerBaseUnit,
                    (current.PricePerBaseUnit - previous) / previous, current.Date);
            })
            .Where(change => Math.Abs(change.ChangePercent) >= minChange)
            .OrderByDescending(change => change.ChangePercent)
            .ThenBy(change => change.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
    }

    private async Task<decimal> GetAlertThresholdAsync(int restaurantId)
    {
        await using var db = await factory.CreateDbContextAsync();

        // Restaurant n'est pas filtré par restaurant courant : on le cible explicitement.
        return await db.Restaurants
                   .Where(r => r.Id == restaurantId)
                   .Select(r => (decimal?)r.FoodCostAlertThreshold)
                   .FirstOrDefaultAsync()
               ?? throw new InvalidOperationException($"Restaurant {restaurantId} introuvable.");
    }
}
