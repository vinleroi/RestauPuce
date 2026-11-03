using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class DemoSeederTests(PostgresFixture postgres)
{
    private const string DemoEmail = "demo@chefos.local";
    private const string DemoPassword = "ChefDemo2026!";
    private const string BistroSlug = "le-petit-bistro";

    /// <summary>
    /// Hôte minimal : infrastructure branchée sur la base de test, Identity, restaurant courant fixe (aucun, comme le fait le seeder)
    /// et section <c>Demo</c> fournie par la configuration, comme dans une vraie application.
    /// </summary>
    internal static ServiceProvider BuildHost(
        DbContextOptions<ChefOsDbContext> options, string password = DemoPassword, Action<IdentityBuilder>? configureIdentity = null)
    {
        string connectionString;
        using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
            connectionString = db.Database.GetConnectionString()!;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Email"] = DemoEmail,
                ["Demo:Password"] = password
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(connectionString);
        services.AddChefOsDemoSeeder();
        var identity = services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ChefOsDbContext>();
        configureIdentity?.Invoke(identity);

        return services.BuildServiceProvider();
    }

    internal static async Task SeedAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
    }

    internal static async Task<int> GetBistroIdAsync(DbContextOptions<ChefOsDbContext> options)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        return await db.Restaurants.Where(r => r.Slug == BistroSlug).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    public async Task Seed_EmptyDatabase_CreatesBistroWithCompleteCosts()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        var book = await new RecipeService(new TestContextFactory(options, restaurantId)).LoadBookAsync();

        Assert.True(book.Ingredients.Count >= 40, $"{book.Ingredients.Count} ingrédients");
        Assert.All(book.Ingredients, i => Assert.True(i.CurrentPricePerBaseUnit > 0m, $"{i.Name} sans prix"));

        var preparations = book.Recipes.Where(r => r.Kind == RecipeKind.Preparation).Select(r => r.Name).ToList();
        Assert.Equal(
            ["Fond de veau", "Mayonnaise maison", "Pâte à gaufre", "Purée", "Sauce lapin à la bière"],
            preparations.Order().ToList());

        var dishes = book.Recipes.Where(r => r.Kind == RecipeKind.Dish).ToList();
        Assert.True(dishes.Count >= 10, $"{dishes.Count} plats");
        foreach (var required in new[]
                 {
                     "Carbonnade flamande", "Boulets liégeois", "Vol-au-vent", "Moules-frites",
                     "Américain préparé", "Croquettes de crevettes"
                 })
            Assert.Contains(dishes, d => d.Name == required);
        Assert.All(dishes, d => Assert.True(
            d.Lines.Any(l => l.SubRecipeId is not null) || d.Lines.Count >= 3, $"{d.Name} : ni préparation ni 3 ingrédients"));

        var costs = dishes.Select(d => (Dish: d, Cost: book.Cost(d.Id))).ToList();
        Assert.All(costs, c =>
        {
            Assert.True(c.Cost.IsComplete, $"{c.Dish.Name} : coût incomplet");
            Assert.Equal(0.12m, c.Dish.VatRate);
            Assert.True(c.Dish.SellingPriceInclVat > 0m, $"{c.Dish.Name} sans prix de vente");
            Assert.InRange(c.Cost.FoodCostPercent!.Value, 0.20m, 0.50m);
        });

        // Un seul plat est volontairement au-dessus du seuil d'alerte (0,35) pour peupler la liste « à surveiller » ;
        // tous les autres restent sous 0,40 (ici même sous le seuil).
        var overThreshold = costs.Where(c => c.Cost.FoodCostPercent > 0.35m).ToList();
        Assert.Single(overThreshold);
        Assert.Equal("Croquettes de crevettes", overThreshold[0].Dish.Name);
        Assert.All(costs.Except(overThreshold), c => Assert.True(c.Cost.FoodCostPercent <= 0.40m, c.Dish.Name));
    }

    [Fact]
    public async Task Seed_EmptyDatabase_FillsIngredientsRealistically()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        var book = await new RecipeService(new TestContextFactory(options, restaurantId)).LoadBookAsync();
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));

        Assert.Equal(3, await db.Suppliers.CountAsync());
        Assert.All(book.Ingredients, i =>
        {
            Assert.InRange(i.YieldPercent, 1m, 100m);
            Assert.NotNull(i.SupplierId);
        });

        // Rendements crédibles : légumes 80-90 %, viandes à parer 85-95 %, poissons entiers 50-65 %.
        Assert.All(book.Ingredients.Where(i => i.Category == "Légumes"), i => Assert.InRange(i.YieldPercent, 80m, 90m));
        Assert.InRange(book.Ingredients.Single(i => i.Name == "Paleron de bœuf").YieldPercent, 85m, 95m);
        Assert.InRange(book.Ingredients.Single(i => i.Name == "Filet de poulet fermier").YieldPercent, 85m, 95m);
        Assert.InRange(book.Ingredients.Single(i => i.Name == "Cabillaud entier").YieldPercent, 50m, 65m);

        // Les allergènes se propagent des ingrédients et des préparations jusqu'aux plats.
        EuAllergen AllergensOf(string dish) =>
            book.Allergens(book.Recipes.Single(r => r.Kind == RecipeKind.Dish && r.Name == dish));
        Assert.True(AllergensOf("Carbonnade flamande").HasFlag(EuAllergen.Gluten));
        Assert.True(AllergensOf("Croquettes de crevettes").HasFlag(EuAllergen.Crustaceans));
        Assert.True(AllergensOf("Moules-frites").HasFlag(EuAllergen.Molluscs));
        Assert.True(AllergensOf("Américain préparé").HasFlag(EuAllergen.Eggs));
    }

    [Fact]
    public async Task Seed_EmptyDatabase_RecordsPriceHistoryOverLast90Days()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var ingredients = await db.Ingredients.ToListAsync();
        var prices = await db.IngredientPrices.ToListAsync();

        var now = DateTime.UtcNow;
        Assert.All(prices, p =>
        {
            Assert.Equal(DateTimeKind.Utc, p.Date.Kind);
            Assert.InRange(p.Date, now.AddDays(-90), now);
        });

        var byIngredient = prices.GroupBy(p => p.IngredientId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Date).ToList());
        Assert.All(ingredients, i => Assert.True(byIngredient.ContainsKey(i.Id), $"{i.Name} sans historique"));
        Assert.True(byIngredient.Values.Count(h => h.Count >= 4) >= 3, "au moins 3 ingrédients avec 4 prix ou plus");

        // Le prix courant d'un ingrédient est son prix le plus récent.
        Assert.All(ingredients, i => Assert.Equal(i.CurrentPricePerBaseUnit, byIngredient[i.Id][^1].PricePerBaseUnit));
    }

    [Fact]
    public async Task Seed_Twice_IsIdempotent()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);
        var restaurantId = await GetBistroIdAsync(options);
        var before = await CountRowsAsync(options, restaurantId);

        await SeedAsync(provider);

        Assert.Equal(restaurantId, await GetBistroIdAsync(options));
        Assert.Equal(before, await CountRowsAsync(options, restaurantId));
    }

    private static async Task<int[]> CountRowsAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        return
        [
            await db.Restaurants.CountAsync(), await db.Memberships.CountAsync(), await db.Users.CountAsync(),
            await db.Suppliers.CountAsync(), await db.Ingredients.CountAsync(), await db.IngredientPrices.CountAsync(),
            await db.Recipes.CountAsync(), await db.RecipeLines.CountAsync()
        ];
    }

    [Fact]
    public async Task Seed_DemoUserCanSignIn()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByEmailAsync(DemoEmail);
        Assert.NotNull(user);
        Assert.True(await users.CheckPasswordAsync(user, DemoPassword));
        Assert.False(await users.CheckPasswordAsync(user, "mauvais-mot-de-passe"));
        Assert.True(user.EmailConfirmed);
        Assert.Equal(restaurantId, user.ActiveRestaurantId);

        var memberships = await scope.ServiceProvider.GetRequiredService<RestaurantService>().GetMembershipsAsync(user.Id);
        var membership = Assert.Single(memberships);
        Assert.Equal(new MembershipSummary(restaurantId, "Le Petit Bistro", MembershipRole.Owner), membership);
    }

    [Fact]
    public async Task Seed_ShowsWatchedDishOnDashboard()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        await SeedAsync(provider);
        var restaurantId = await GetBistroIdAsync(options);

        // Le tableau de bord du restaurant de démo : même assemblage que les hôtes, avec le restaurant courant du bistro.
        var factory = new TestContextFactory(options, restaurantId);
        var summary = await new DashboardService(new RecipeService(factory), factory, new FixedTenantContext(restaurantId))
            .GetSummaryAsync();

        Assert.True(summary.DishCount >= 10);
        Assert.Equal(5, summary.PreparationCount);
        var watched = Assert.Single(summary.WatchList);
        Assert.Equal("Croquettes de crevettes", watched.Name);
    }

    [Fact]
    public async Task Seed_DemoUserRejectedByIdentity_ThrowsAndCreatesNothing()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options, password: "x");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(provider));

        Assert.StartsWith("Impossible de créer l'utilisateur de démo", error.Message);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        Assert.Empty(await db.Restaurants.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task Seed_FailureAfterRestaurantCreated_LeavesNoHalfBistroAndCanBeRetried()
    {
        var options = await postgres.CreateDatabaseAsync();

        // Refuse toute mise à jour de l'utilisateur qui lui donne un restaurant actif : l'échec survient après la création du restaurant.
        await using (var failing = BuildHost(options, configureIdentity: identity => identity.AddUserValidator<RejectActiveRestaurantValidator>()))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(failing));
            Assert.Contains("restaurant actif refusé", error.Message);
        }

        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
        {
            Assert.Empty(await db.Restaurants.ToListAsync());
            Assert.Empty(await db.Memberships.ToListAsync());
        }

        await using var healthy = BuildHost(options);
        await SeedAsync(healthy);

        var restaurantId = await GetBistroIdAsync(options);
        using var scope = healthy.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(restaurantId, (await users.FindByEmailAsync(DemoEmail))!.ActiveRestaurantId);
        await using var check = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        Assert.Single(await check.Users.ToListAsync());
        Assert.NotEmpty(await check.Recipes.ToListAsync());
    }

    private sealed class RejectActiveRestaurantValidator : IUserValidator<ApplicationUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
            Task.FromResult(user.ActiveRestaurantId is null
                ? IdentityResult.Success
                : IdentityResult.Failed(new IdentityError { Description = "restaurant actif refusé" }));
    }

    [Fact]
    public async Task AddChefOsInfrastructure_RegistersScopedServices()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        foreach (var serviceType in new[]
                 {
                     typeof(RestaurantService), typeof(IngredientService), typeof(SupplierService),
                     typeof(RecipeService), typeof(DashboardService), typeof(DemoSeeder)
                 })
        {
            using var first = provider.CreateScope();
            using var second = provider.CreateScope();

            var a = first.ServiceProvider.GetRequiredService(serviceType);
            Assert.Same(a, first.ServiceProvider.GetRequiredService(serviceType));
            Assert.NotSame(a, second.ServiceProvider.GetRequiredService(serviceType));
        }

        using var scope = provider.CreateScope();
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<ChefOsDbContext>(),
            scope.ServiceProvider.GetRequiredService<ChefOsDbContext>());
        Assert.Equal(DemoEmail, scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DemoOptions>>().Value.Email);
    }
}
