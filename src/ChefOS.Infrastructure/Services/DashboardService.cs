using ChefOS.Core.Costing;
using ChefOS.Core.Recipes;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Chiffres clés du restaurant courant. Seuls les plats et préparations non archivés comptent : un élément archivé est
/// retiré de la carte et ne doit ni peser sur la moyenne ni apparaître dans les plats à surveiller.
/// </summary>
public class DashboardService(RecipeService recipes, IDbContextFactory<ChefOsDbContext> factory, ITenantContext tenant)
{
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
