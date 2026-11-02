using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class DashboardServiceTests(PostgresFixture postgres)
{
    private static DashboardService CreateService(DbContextOptions<ChefOsDbContext> options, int? restaurantId) =>
        new(new RecipeService(new TestContextFactory(options, restaurantId)),
            new TestContextFactory(options, restaurantId),
            new FixedTenantContext(restaurantId));

    private static RecipeService CreateRecipeService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    /// <summary>Ingrédient en grammes à 0,01 € le gramme (rendement 100 %), ou sans prix.</summary>
    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, decimal? pricePerGram = 0.01m)
    {
        var ingredient = await new IngredientService(new TestContextFactory(options, restaurantId)).SaveAsync(
            new IngredientInput(null, name, "Divers", EuAllergen.None, BaseUnit.Gram, pricePerGram, 100m, null, null));
        return ingredient.Id;
    }

    /// <summary>
    /// Plat à 11,20 € TTC (10,00 € HT) dont le coût est <paramref name="grams"/> × 0,01 € :
    /// le food cost vaut donc <paramref name="grams"/> / 1 000 (200 g → 20 %).
    /// </summary>
    private static async Task<Recipe> AddDishAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, int ingredientId, decimal grams)
    {
        IReadOnlyList<RecipeLineInput> lines = grams == 0m ? [] : [new(ingredientId, null, grams, MeasureUnit.G)];
        return await CreateRecipeService(options, restaurantId).SaveAsync(
            new RecipeInput(null, name, RecipeKind.Dish, "Plats", 11.20m, 0.12m, null, null, null, lines));
    }

    private static async Task SetThresholdAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, decimal threshold)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        await db.Restaurants.Where(r => r.Id == restaurantId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FoodCostAlertThreshold, threshold));
    }

    [Fact]
    public async Task Summary_AverageExcludesIncompleteDishes()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var base01 = await AddIngredientAsync(options, restaurant, "Base");
        var safran = await AddIngredientAsync(options, restaurant, "Safran", pricePerGram: null);
        await AddDishAsync(options, restaurant, "Stoemp", base01, 200m);            // 20 %
        await AddDishAsync(options, restaurant, "Carbonnade", base01, 400m);        // 40 %
        await AddDishAsync(options, restaurant, "Paella", safran, 300m);            // coût incomplet : exclu de la moyenne
        await CreateRecipeService(options, restaurant).SaveAsync(new RecipeInput(
            null, "Fond", RecipeKind.Preparation, null, null, null, 1000m, BaseUnit.Milliliter, null, []));

        var summary = await service.GetSummaryAsync();

        Assert.Equal(3, summary.DishCount);
        Assert.Equal(1, summary.PreparationCount);
        Assert.Equal(0.30m, summary.AverageFoodCost);                               // (20 % + 40 %) / 2
    }

    [Fact]
    public async Task Summary_AverageExcludesZeroFoodCostAndArchivedDishes()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var base01 = await AddIngredientAsync(options, restaurant, "Base");
        await AddDishAsync(options, restaurant, "Stoemp", base01, 200m);
        await AddDishAsync(options, restaurant, "Brouillon", base01, 0m);           // sans ligne : food cost nul
        var retired = await AddDishAsync(options, restaurant, "Ancien plat", base01, 900m);
        await CreateRecipeService(options, restaurant).SetArchivedAsync(retired.Id, true);

        var summary = await service.GetSummaryAsync();

        Assert.Equal(2, summary.DishCount);                                         // le plat archivé n'est pas compté
        Assert.Equal(0.20m, summary.AverageFoodCost);
        Assert.Empty(summary.WatchList);                                            // ni surveillé
    }

    [Fact]
    public async Task Summary_NoDishes_HasNoAverage()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");

        var summary = await CreateService(options, restaurant).GetSummaryAsync();

        Assert.Equal(0, summary.DishCount);
        Assert.Equal(0, summary.PreparationCount);
        Assert.Null(summary.AverageFoodCost);
        Assert.Empty(summary.WatchList);
    }

    [Fact]
    public async Task Summary_WatchList_IncompleteFirstThenHighestFoodCost()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro"); // seuil par défaut : 35 %
        var service = CreateService(options, restaurant);
        var base01 = await AddIngredientAsync(options, restaurant, "Base");
        var safran = await AddIngredientAsync(options, restaurant, "Safran", pricePerGram: null);
        var alert38 = await AddDishAsync(options, restaurant, "Boulets", base01, 380m);
        var alert50 = await AddDishAsync(options, restaurant, "Vol-au-vent", base01, 500m);
        var incompleteZ = await AddDishAsync(options, restaurant, "Zeste", safran, 10m);
        var incompleteA = await AddDishAsync(options, restaurant, "Anchois", safran, 10m);
        await AddDishAsync(options, restaurant, "Stoemp", base01, 200m);            // Good
        await AddDishAsync(options, restaurant, "Moules", base01, 300m);            // Warning (30 %, sous le seuil)
        await AddDishAsync(options, restaurant, "Limite", base01, 350m);            // exactement au seuil : pas en alerte

        var summary = await service.GetSummaryAsync();

        Assert.Equal(
            [incompleteA.Id, incompleteZ.Id, alert50.Id, alert38.Id],
            summary.WatchList.Select(w => w.RecipeId));
        Assert.Equal(["Anchois", "Zeste", "Vol-au-vent", "Boulets"], summary.WatchList.Select(w => w.Name));
        Assert.Equal(
            [FoodCostLevel.Incomplete, FoodCostLevel.Incomplete, FoodCostLevel.Alert, FoodCostLevel.Alert],
            summary.WatchList.Select(w => w.Level));
        Assert.Equal([null, null, 0.50m, 0.38m], summary.WatchList.Select(w => w.FoodCostPercent));
    }

    [Fact]
    public async Task Summary_UsesCurrentRestaurantThreshold()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var baseA = await AddIngredientAsync(options, a, "Base");
        var baseB = await AddIngredientAsync(options, b, "Base");
        await AddDishAsync(options, a, "Stoemp", baseA, 300m);                      // 30 %
        await AddDishAsync(options, b, "Piraté", baseB, 800m);                      // 80 %, d'un autre restaurant
        await SetThresholdAsync(options, b, 0.10m);

        var summary = await CreateService(options, a).GetSummaryAsync();

        Assert.Equal(1, summary.DishCount);
        Assert.Equal(0.30m, summary.AverageFoodCost);
        Assert.Empty(summary.WatchList);                                            // 30 % est sous le seuil de A (35 %)

        await SetThresholdAsync(options, a, 0.25m);

        var strict = await CreateService(options, a).GetSummaryAsync();
        var item = Assert.Single(strict.WatchList);
        Assert.Equal("Stoemp", item.Name);
        Assert.Equal(FoodCostLevel.Alert, item.Level);
        Assert.Equal(0.30m, item.FoodCostPercent);
    }

    [Fact]
    public async Task Summary_WithoutCurrentRestaurant_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(options, null).GetSummaryAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(options, restaurant + 1000).GetSummaryAsync());
    }
}
