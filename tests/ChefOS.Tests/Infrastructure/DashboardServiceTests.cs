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

    /// <summary>Ingrédient en grammes (ou dans <paramref name="baseUnit"/>) à 0,01 € l'unité de base (rendement 100 %), ou sans prix.</summary>
    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, decimal? pricePerGram = 0.01m,
        BaseUnit baseUnit = BaseUnit.Gram)
    {
        var ingredient = await new IngredientService(new TestContextFactory(options, restaurantId)).SaveAsync(
            new IngredientInput(null, name, "Divers", EuAllergen.None, baseUnit, pricePerGram, 100m, null, null));
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

    // ── Variations de prix récentes ──────────────────────────────────────────

    /// <summary>Date UTC « il y a <paramref name="days"/> jours » : les marges des tests se comptent en jours, jamais à la seconde.</summary>
    private static DateTime DaysAgo(double days) => DateTime.UtcNow.AddDays(-days);

    /// <summary>Ingrédient sans prix initial (donc sans historique), pour maîtriser chaque entrée datée avec <see cref="AddPriceAsync"/>.</summary>
    private static Task<int> AddUnpricedAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, BaseUnit baseUnit = BaseUnit.Gram) =>
        AddIngredientAsync(options, restaurantId, name, pricePerGram: null, baseUnit);

    private static Task AddPriceAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, int ingredientId, decimal price, DateTime dateUtc) =>
        new IngredientService(new TestContextFactory(options, restaurantId))
            .SetPriceAsync(ingredientId, price, PriceSource.Receipt, null, dateUtc);

    /// <summary>Ingrédient dont les deux dernières entrées sont <paramref name="previous"/> (il y a 10 jours) puis <paramref name="current"/> (il y a 2 jours).</summary>
    private static async Task<int> AddChangedAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, decimal previous, decimal current)
    {
        var id = await AddUnpricedAsync(options, restaurantId, name);
        await AddPriceAsync(options, restaurantId, id, previous, DaysAgo(10));
        await AddPriceAsync(options, restaurantId, id, current, DaysAgo(2));
        return id;
    }

    [Fact]
    public async Task PriceChanges_ListsRecentIncreaseOverThreshold()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var beurre = await AddUnpricedAsync(options, restaurant, "Beurre", BaseUnit.Gram);
        var latestDate = DaysAgo(3);
        await AddPriceAsync(options, restaurant, beurre, 0.020m, DaysAgo(60));      // plus ancienne que les deux dernières : ignorée
        await AddPriceAsync(options, restaurant, beurre, 0.008m, DaysAgo(20));      // avant-dernière : prix précédent
        await AddPriceAsync(options, restaurant, beurre, 0.010m, latestDate);       // dernière : prix courant (+25 %)

        var changes = await service.GetRecentPriceChangesAsync();

        var change = Assert.Single(changes);
        Assert.Equal(beurre, change.IngredientId);
        Assert.Equal("Beurre", change.Name);
        Assert.Equal(BaseUnit.Gram, change.BaseUnit);
        Assert.Equal(0.008m, change.PreviousPrice);
        Assert.Equal(0.010m, change.CurrentPrice);
        Assert.Equal(0.25m, change.ChangePercent);
        Assert.Equal(latestDate, change.Date, TimeSpan.FromSeconds(1));             // la date de la dernière entrée
        Assert.Equal(DateTimeKind.Utc, change.Date.Kind);
    }

    [Fact]
    public async Task PriceChanges_IgnoresOldAndSmallChanges()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var retenu = await AddChangedAsync(options, restaurant, "Retenu", 0.010m, 0.012m);               // +20 %, il y a 2 jours
        await AddChangedAsync(options, restaurant, "Petite hausse", 0.0100m, 0.0104m);                   // +4 % : sous le seuil de 5 %
        await AddChangedAsync(options, restaurant, "Petite baisse", 0.0100m, 0.0096m);                   // -4 %
        var ancien = await AddUnpricedAsync(options, restaurant, "Ancien");                              // +100 %, mais il y a 40 jours
        await AddPriceAsync(options, restaurant, ancien, 0.010m, DaysAgo(70));
        await AddPriceAsync(options, restaurant, ancien, 0.020m, DaysAgo(40));

        var changes = await service.GetRecentPriceChangesAsync();

        Assert.Equal([retenu], changes.Select(c => c.IngredientId));
    }

    [Fact]
    public async Task PriceChanges_SkipsIngredientsWithoutTwoEntriesOrWithZeroPreviousPrice()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var retenu = await AddChangedAsync(options, restaurant, "Retenu", 0.010m, 0.015m);
        await AddUnpricedAsync(options, restaurant, "Sans prix");                                        // aucune entrée
        var single = await AddUnpricedAsync(options, restaurant, "Un seul prix");                        // une seule entrée : rien à comparer
        await AddPriceAsync(options, restaurant, single, 0.010m, DaysAgo(1));
        await AddChangedAsync(options, restaurant, "Précédent nul", 0m, 0.010m);                         // division par zéro évitée

        var changes = await service.GetRecentPriceChangesAsync();

        Assert.Equal([retenu], changes.Select(c => c.IngredientId));
    }

    [Fact]
    public async Task PriceChanges_ExcludesArchivedIngredientsAndOtherRestaurants()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var retenu = await AddChangedAsync(options, a, "Retenu", 0.010m, 0.012m);
        var archived = await AddChangedAsync(options, a, "Archivé", 0.010m, 0.020m);
        await new IngredientService(new TestContextFactory(options, a)).SetArchivedAsync(archived, true);
        await AddChangedAsync(options, b, "Autre restaurant", 0.010m, 0.030m);

        var changes = await CreateService(options, a).GetRecentPriceChangesAsync();

        Assert.Equal([retenu], changes.Select(c => c.IngredientId));
        Assert.Equal(["Autre restaurant"], (await CreateService(options, b).GetRecentPriceChangesAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task PriceChanges_IncludesDecreasesSortedAfterIncreases()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        await AddChangedAsync(options, restaurant, "Baisse", 0.010m, 0.008m);                            // -20 %
        await AddChangedAsync(options, restaurant, "Hausse faible", 0.010m, 0.011m);                     // +10 %
        await AddChangedAsync(options, restaurant, "Forte baisse", 0.010m, 0.005m);                      // -50 %
        await AddChangedAsync(options, restaurant, "Forte hausse", 0.010m, 0.015m);                      // +50 %

        var changes = await CreateService(options, restaurant).GetRecentPriceChangesAsync();

        Assert.Equal(["Forte hausse", "Hausse faible", "Baisse", "Forte baisse"], changes.Select(c => c.Name));
        Assert.Equal([0.5m, 0.1m, -0.2m, -0.5m], changes.Select(c => c.ChangePercent));
    }

    [Fact]
    public async Task PriceChanges_ComparesTwoLatestEntriesByDateThenId()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        // Saisies dans le désordre : l'entrée la plus récente est la première enregistrée.
        var outOfOrder = await AddUnpricedAsync(options, restaurant, "Hors ordre");
        await AddPriceAsync(options, restaurant, outOfOrder, 0.012m, DaysAgo(2));
        await AddPriceAsync(options, restaurant, outOfOrder, 0.010m, DaysAgo(9));

        // Même date : la dernière enregistrée (identifiant le plus grand) est la plus récente.
        var sameInstant = await AddUnpricedAsync(options, restaurant, "Même instant");
        var instant = DaysAgo(3);
        await AddPriceAsync(options, restaurant, sameInstant, 0.100m, DaysAgo(20));
        await AddPriceAsync(options, restaurant, sameInstant, 0.010m, instant);
        await AddPriceAsync(options, restaurant, sameInstant, 0.015m, instant);

        var changes = await service.GetRecentPriceChangesAsync();

        Assert.Equal(["Même instant", "Hors ordre"], changes.Select(c => c.Name));                       // +50 % puis +20 %
        Assert.Equal([(0.010m, 0.015m), (0.010m, 0.012m)], changes.Select(c => (c.PreviousPrice, c.CurrentPrice)));
    }

    [Fact]
    public async Task PriceChanges_ChangePercentIsNotRounded()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        await AddChangedAsync(options, restaurant, "Tiers", 0.003m, 0.004m);

        var change = Assert.Single(await CreateService(options, restaurant).GetRecentPriceChangesAsync());

        Assert.Equal((0.004m - 0.003m) / 0.003m, change.ChangePercent);                                  // 0,3333… et non 0,33
    }

    [Fact]
    public async Task PriceChanges_ThresholdIsInclusiveAndBothBoundsAreConfigurable()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        await AddChangedAsync(options, restaurant, "Pile 5 %", 0.100m, 0.105m);                          // +5 % exactement, il y a 2 jours
        var older = await AddUnpricedAsync(options, restaurant, "Il y a 29 jours");
        await AddPriceAsync(options, restaurant, older, 0.010m, DaysAgo(40));
        await AddPriceAsync(options, restaurant, older, 0.020m, DaysAgo(29));                            // +100 %, dans les 30 jours
        var tooOld = await AddUnpricedAsync(options, restaurant, "Il y a 31 jours");
        await AddPriceAsync(options, restaurant, tooOld, 0.010m, DaysAgo(40));
        await AddPriceAsync(options, restaurant, tooOld, 0.020m, DaysAgo(31));                           // +100 %, hors des 30 jours

        Assert.Equal(["Il y a 29 jours", "Pile 5 %"], (await service.GetRecentPriceChangesAsync()).Select(c => c.Name));
        Assert.Equal(["Pile 5 %"], (await service.GetRecentPriceChangesAsync(days: 7)).Select(c => c.Name));
        Assert.Equal(["Il y a 29 jours"], (await service.GetRecentPriceChangesAsync(minChange: 0.06m)).Select(c => c.Name));
        Assert.Equal(
            ["Il y a 29 jours", "Il y a 31 jours", "Pile 5 %"],                                          // +100 % à égalité : par nom
            (await service.GetRecentPriceChangesAsync(days: 35)).Select(c => c.Name));
    }
}
