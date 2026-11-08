using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

        // Les trois fournisseurs des ingrédients, et Metro, chez qui le bistro fait ses achats d'épicerie et de boissons (tickets appliqués).
        Assert.Equal(4, await db.Suppliers.CountAsync());
        Assert.Contains(await db.Suppliers.ToListAsync(), supplier => supplier.Name == "Metro");
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

    [Fact]
    public async Task Seed_EmptyDatabase_PublishesTheVitrineWithServicesAndPublicDishes()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var profile = await db.VitrineProfiles.SingleAsync();
        Assert.True(profile.IsPublished);
        Assert.True(profile.ReservationsOpen);
        Assert.Equal("Liège", profile.City);
        Assert.Null(profile.CoverImageId);
        Assert.Empty(await db.VitrineImages.ToListAsync());

        var schedules = await db.ServiceSchedules.ToListAsync();
        Assert.Equal(10, schedules.Count);
        Assert.DoesNotContain(schedules, s => s.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Monday);
        Assert.Equal(5, schedules.Count(s => s is { Name: "Midi", Capacity: 40, SlotMinutes: 30 }));
        Assert.Equal(5, schedules.Count(s => s is { Name: "Soir", Capacity: 50, SlotMinutes: 30 }));
        Assert.Single(await db.ClosureDays.ToListAsync());

        var dishes = await db.Recipes.Where(r => r.Kind == RecipeKind.Dish).ToListAsync();
        Assert.Equal(12, dishes.Count);
        Assert.All(dishes, d =>
        {
            Assert.True(d.ShowOnVitrine, d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.PublicDescription), d.Name);
        });
    }

    [Fact]
    public async Task Seed_EmptyDatabase_LoadsTheWineLibraryAndTheWineListWithPairingsAndOneAiDraft()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        Assert.True(await db.WineReferences.CountAsync() >= 60);

        var wines = await db.WineListItems.ToListAsync();
        Assert.Equal(15, wines.Count(w => w.IsPublished));
        var draft = Assert.Single(wines, w => !w.IsPublished);
        Assert.True(draft.DescriptionIsAiDraft);
        Assert.Single(wines, w => w.DescriptionIsAiDraft);
        Assert.True(wines.Count(w => w.IsPublished && w.PriceGlass is not null) >= 2);
        Assert.Contains(wines, w => w.IsPublished && w.Color == WineColor.Effervescent && w.Country == "Belgique");
        Assert.All(wines.Where(w => w.IsPublished), w =>
        {
            Assert.NotNull(w.WineReferenceId);
            Assert.True(w.PriceBottle > 0m);
        });
        Assert.All(wines.GroupBy(w => w.Color), g => Assert.Equal(g.Count(), g.Select(w => w.Position).Distinct().Count()));

        var pairings = await db.WineListItemPairings.ToListAsync();
        Assert.All(wines.Where(w => w.IsPublished), w => Assert.InRange(pairings.Count(p => p.WineListItemId == w.Id), 1, 2));
    }

    [Fact]
    public async Task Seed_EmptyDatabase_SeedsUpcomingReservations_WithAGroupOfEightWhoseDishesAreFrozen()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);

        await SeedAsync(provider);

        var restaurantId = await GetBistroIdAsync(options);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var reservations = await db.Reservations.Include(r => r.Guests).ThenInclude(g => g.Dishes).ToListAsync();
        var dishPrices = await db.Recipes.Where(r => r.Kind == RecipeKind.Dish).ToDictionaryAsync(r => r.Id);

        Assert.Equal(6, reservations.Count(r => r.Status == ReservationStatus.Confirmed && r.PartySize is >= 2 and <= 4));
        Assert.Single(reservations, r => r.Status == ReservationStatus.PendingPayment);
        Assert.Single(reservations, r => r.Status == ReservationStatus.Cancelled);
        Assert.Equal(9, reservations.Count);
        Assert.All(reservations, r => Assert.Equal(43, r.PublicToken.Length));
        Assert.Equal(9, reservations.Select(r => r.PublicToken).Distinct().Count());

        var group = Assert.Single(reservations, r => r.PartySize == 8);
        Assert.Equal(ReservationStatus.Confirmed, group.Status);
        Assert.Equal(8, group.Guests.Count);
        Assert.All(group.Guests, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.FirstName));
            Assert.False(string.IsNullOrWhiteSpace(g.LastName));
            Assert.NotEmpty(g.Dishes);
            Assert.All(g.Dishes, d =>
            {
                Assert.Equal(dishPrices[d.RecipeId].SellingPriceInclVat, d.UnitPriceInclVat);
                Assert.Equal(dishPrices[d.RecipeId].VatRate, d.VatRate);
            });
        });
        var total = group.Guests.SelectMany(g => g.Dishes).Sum(d => d.UnitPriceInclVat * d.Quantity);
        Assert.Equal(total, group.PreorderTotalInclVat);
        Assert.Equal(Math.Round(total * 0.30m, 2, MidpointRounding.AwayFromZero), group.AmountDue);

        // Paiements : tous en démo, avec la ligne de passerelle qui correspond (le remboursement marche depuis le back).
        var payments = await db.Payments.ToListAsync();
        Assert.All(payments, p => Assert.Equal("Demo", p.Gateway));
        var gateway = await db.DemoGatewayPayments.ToDictionaryAsync(p => p.Id);
        Assert.All(payments, p => Assert.True(gateway.ContainsKey(p.GatewayPaymentId!)));
        Assert.All(payments.Where(p => p.Status is PaymentStatus.Paid or PaymentStatus.Refunded),
            p => Assert.Equal(GatewayStatus.Paid, gateway[p.GatewayPaymentId!].Status));
        var cancelled = Assert.Single(reservations, r => r.Status == ReservationStatus.Cancelled);
        var refund = Assert.Single(payments, p => p.ReservationId == cancelled.Id);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
        Assert.Equal(refund.Amount, gateway[refund.GatewayPaymentId!].RefundedAmount);
        Assert.All(reservations.Where(r => r.Status == ReservationStatus.Confirmed), r =>
        {
            var paid = Assert.Single(payments, p => p.ReservationId == r.Id);
            Assert.Equal(PaymentStatus.Paid, paid.Status);
            Assert.Equal(r.AmountDue, paid.Amount);
            Assert.Equal(r.PartySize > 5 ? PaymentKind.Prepayment : PaymentKind.Deposit, paid.Kind);
        });

        // Le paiement en attente est plus vieux que le délai : il expirera à la première lecture.
        var pending = Assert.Single(reservations, r => r.Status == ReservationStatus.PendingPayment);
        Assert.True(DateTime.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(30));
        Assert.True(pending.StartsAtUtc > DateTime.UtcNow);

        Assert.All(reservations.Where(r => r.Status != ReservationStatus.Cancelled).GroupBy(r => (r.ServiceName, r.StartsAtUtc.Date)),
            g => Assert.True(g.Sum(r => r.PartySize) < 40, $"{g.Key} : {g.Sum(r => r.PartySize)} couverts"));
    }

    [Fact]
    public async Task Seed_EmptyDatabase_LeavesSlotsAvailableInTheComingWeek_AndExpiresTheStalePendingPayment()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        await SeedAsync(provider);
        var restaurantId = await GetBistroIdAsync(options);

        var factory = new TestContextFactory(options, restaurantId);
        var clock = TimeProvider.System;
        var gateway = new DemoPaymentGateway("https://vitrine.test", factory, clock);
        var email = new RecordingEmailSender();
        var payments = new PaymentService(factory, gateway, clock, NullLogger<PaymentService>.Instance, email);
        var service = new ReservationService(factory, clock, payments, email);

        var today = RestaurantClock.Today(DateTime.UtcNow, "Europe/Brussels");
        var daysWithSlots = 0;
        for (var i = 0; i <= 8; i++)
            if ((await service.GetSlotsAsync(today.AddDays(i), 2)).Count > 0)
                daysWithSlots++;
        Assert.True(daysWithSlots >= 1, "aucun créneau cette semaine");

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        Assert.Single(await db.Reservations.Where(r => r.Status == ReservationStatus.Expired).ToListAsync());
    }

    private static async Task<int[]> CountRowsAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        return
        [
            await db.Restaurants.CountAsync(), await db.Memberships.CountAsync(), await db.Users.CountAsync(),
            await db.Suppliers.CountAsync(), await db.Ingredients.CountAsync(), await db.IngredientPrices.CountAsync(),
            await db.Recipes.CountAsync(), await db.RecipeLines.CountAsync(),
            await db.SalesEntries.CountAsync(), await db.Orders.CountAsync(), await db.OrderLines.CountAsync(),
            await db.WasteEntries.CountAsync(), await db.StockCounts.CountAsync(), await db.StockCountLines.CountAsync(),
            await db.Receipts.CountAsync(), await db.ReceiptLines.CountAsync(),
            await db.ServiceSchedules.CountAsync(), await db.ClosureDays.CountAsync(), await db.WineListItems.CountAsync(),
            await db.WineListItemPairings.CountAsync(), await db.Reservations.CountAsync(), await db.ReservationGuests.CountAsync(),
            await db.ReservationGuestDishes.CountAsync(), await db.Payments.CountAsync(), await db.DemoGatewayPayments.CountAsync(),
            await db.VitrineProfiles.CountAsync(), await db.WineReferences.CountAsync()
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
        var dashboard = new DashboardService(new RecipeService(factory), new StatsService(factory), factory, new FixedTenantContext(restaurantId));
        var summary = await dashboard.GetSummaryAsync();

        Assert.True(summary.DishCount >= 10);
        Assert.Equal(5, summary.PreparationCount);
        var watched = Assert.Single(summary.WatchList);
        Assert.Equal("Croquettes de crevettes", watched.Name);

        // Les ventes des 7 derniers jours (saisies de J-7 à J-1 incluses dans la fenêtre J-6 à J, commandes) font vivre les chiffres de la semaine.
        var week = await dashboard.GetLastSevenDaysAsync();
        Assert.True(week.RevenueExclVat > 5_000m, $"{week.RevenueExclVat} € de CA HT sur 7 jours");
        Assert.True(week.GrossMargin > 0.6m * week.RevenueExclVat);
        Assert.False(week.CostIncomplete);
    }

    // ── Activité : ventes, commandes, pertes, tickets et inventaires ─────────

    private static DateTime Today => DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

    /// <summary>Les statistiques des 4 dernières semaines (J-27 à J), la période par défaut de la page /stats.</summary>
    private async Task<(DbContextOptions<ChefOsDbContext> Options, int RestaurantId, PeriodStats Stats)> SeedAndReadLastFourWeeksAsync()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        await SeedAsync(provider);
        var restaurantId = await GetBistroIdAsync(options);

        var stats = await new StatsService(new TestContextFactory(options, restaurantId)).GetAsync(Today.AddDays(-27), Today);
        return (options, restaurantId, stats);
    }

    [Fact]
    public async Task Seed_EmptyDatabase_PopulatesTheFourMenuEngineeringQuadrantsOverTheLastFourWeeks()
    {
        var (_, _, stats) = await SeedAndReadLastFourWeeksAsync();

        Assert.Equal(12, stats.Dishes.Count);
        Assert.All(stats.Dishes, d => Assert.False(d.CostIncomplete, d.Name));
        var byClass = stats.Dishes.GroupBy(d => d.Class).ToDictionary(g => g.Key!.Value, g => g.Select(d => d.Name).ToList());
        Assert.Equal(Enum.GetValues<MenuClass>().Order(), byClass.Keys.Order());
        Assert.All(byClass.Values, names => Assert.NotEmpty(names));

        // Le plat à surveiller (food cost > 35 %) se vend bien mais rapporte peu : une vache à lait.
        Assert.Contains("Croquettes de crevettes", byClass[MenuClass.VacheALait]);
        Assert.Contains("Moules-frites", byClass[MenuClass.Etoile]);
    }

    [Fact]
    public async Task Seed_EmptyDatabase_GivesTheLastFourWeeksCoherentFigures()
    {
        var (_, _, stats) = await SeedAndReadLastFourWeeksAsync();

        Assert.Equal(28, stats.RevenueByDay.Count);
        Assert.InRange(stats.RevenueExclVat, 20_000m, 60_000m);
        Assert.True(stats.RevenueFromEntries > 0m);
        Assert.True(stats.RevenueFromOrders > 0m);
        Assert.Equal(0m, stats.WeeklyTotalsRevenue);                   // le bistro saisit au jour, pas à la semaine
        Assert.False(stats.CostIncomplete);
        Assert.InRange(stats.TheoreticalFoodCost!.Value, 0.22m, 0.32m);
        Assert.True(stats.GrossMargin > 0.6m * stats.RevenueExclVat);

        // Fermé le lundi : aucune vente saisie ce jour-là.
        Assert.All(stats.RevenueByDay.Where(d => d.Day.DayOfWeek == DayOfWeek.Monday), d => Assert.Equal(0m, d.FromEntries));
        Assert.True(stats.RevenueByDay.Count(d => d.FromEntries > 0m) >= 20);
    }

    [Fact]
    public async Task Seed_EmptyDatabase_RecordsEightWeeksOfDailySalesAtTheFrozenDishPrice()
    {
        var (options, restaurantId, _) = await SeedAndReadLastFourWeeksAsync();
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var entries = await db.SalesEntries.ToListAsync();
        var prices = await db.Recipes.ToDictionaryAsync(r => r.Id);

        var today = Today;
        Assert.All(entries, e =>
        {
            Assert.Equal(DateTimeKind.Utc, e.Date.Kind);
            Assert.InRange(e.Date, today.AddDays(-56), today.AddDays(-1));
            Assert.NotEqual(DayOfWeek.Monday, e.Date.DayOfWeek);
            Assert.False(e.IsWeeklyTotal);
            Assert.Equal(ServicePeriod.Journee, e.Service);
            Assert.True(e.Quantity > 0m);
            Assert.Equal(prices[e.RecipeId].SellingPriceInclVat, e.UnitPriceInclVat);
            Assert.Equal(prices[e.RecipeId].VatRate, e.VatRate);
        });
        Assert.Equal(48, entries.Select(e => e.Date).Distinct().Count());          // 8 semaines de 6 jours d'ouverture
        Assert.Equal(entries.Count, entries.Select(e => (e.RecipeId, e.Date)).Distinct().Count());
        Assert.Equal(12, entries.Select(e => e.RecipeId).Distinct().Count());
    }

    [Fact]
    public async Task Seed_EmptyDatabase_TakesAboutFifteenOrdersOverTheLastWeek_OneOfThemCancelled_AndExcludesItFromRevenue()
    {
        var (options, restaurantId, stats) = await SeedAndReadLastFourWeeksAsync();
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var orders = await db.Orders.Include(o => o.Lines).OrderBy(o => o.Number).ToListAsync();

        Assert.InRange(orders.Count, 14, 18);
        Assert.Equal(Enumerable.Range(1, orders.Count), orders.Select(o => o.Number));            // numéros séquentiels 1..n
        Assert.Equal(orders.OrderBy(o => o.CreatedAt).Select(o => o.Number), orders.Select(o => o.Number));
        var now = DateTime.UtcNow;
        Assert.All(orders, o =>
        {
            Assert.InRange(o.CreatedAt, Today.AddDays(-6), now);
            Assert.InRange(o.StatusChangedAt, o.CreatedAt, now);
            Assert.NotEmpty(o.Lines);
        });
        Assert.True(orders.Select(o => o.Status).Distinct().Count() >= 4, "statuts variés");
        Assert.True(orders.Select(o => o.Source).Distinct().Count() >= 3, "sources variées");

        var cancelled = Assert.Single(orders, o => o.Status == OrderStatus.Annulee);
        Assert.NotEmpty(cancelled.Lines);

        // Le chiffre d'affaires des commandes ne compte que les lignes des commandes non annulées, aux prix figés.
        static decimal Revenue(IEnumerable<Order> source) =>
            source.SelectMany(o => o.Lines).Sum(l => l.Quantity * l.UnitPriceInclVat / (1m + l.VatRate));
        var expected = Revenue(orders.Where(o => o.Status != OrderStatus.Annulee));
        Assert.Equal(expected, stats.RevenueFromOrders, 6);
        Assert.NotEqual(Revenue(orders), stats.RevenueFromOrders);
    }

    [Fact]
    public async Task Seed_EmptyDatabase_DeclaresSixWastes_OnIngredientsADishAndAPreparation()
    {
        var (options, restaurantId, stats) = await SeedAndReadLastFourWeeksAsync();
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var waste = await db.WasteEntries.ToListAsync();
        var kinds = await db.Recipes.ToDictionaryAsync(r => r.Id, r => r.Kind);

        Assert.Equal(6, waste.Count);
        Assert.All(waste, w =>
        {
            Assert.True(w.IngredientId is null ^ w.RecipeId is null);
            Assert.InRange(w.Date, Today.AddDays(-28), Today);
            Assert.True(w.Quantity > 0m);
        });
        Assert.Contains(waste, w => w.IngredientId is not null);
        Assert.Contains(waste, w => w.RecipeId is { } id && kinds[id] == RecipeKind.Dish);
        Assert.Contains(waste, w => w.RecipeId is { } id && kinds[id] == RecipeKind.Preparation);
        Assert.True(waste.Select(w => w.Reason).Distinct().Count() >= 4);

        Assert.True(stats.WasteCost > 0m);
        Assert.False(stats.WasteCostIncomplete);
        Assert.Equal(waste.Select(w => w.Reason).Distinct().Count(), stats.Waste.Count);
    }

    [Fact]
    public async Task Seed_EmptyDatabase_ShowsAPlausibleInventoryVarianceBetweenTwoClosedCounts()
    {
        var (_, _, stats) = await SeedAndReadLastFourWeeksAsync();

        var variance = Assert.IsType<VarianceReport>(stats.Variance);
        Assert.Equal(Today.AddDays(-28), variance.OpeningDate);
        Assert.Equal(Today, variance.ClosingDate);
        Assert.NotNull(stats.ActualFoodCost);
        Assert.InRange(stats.ActualFoodCost!.Value, 0.22m, 0.36m);

        var losses = variance.Rows.Where(r => r.VarianceValue > 0m).ToList();
        Assert.True(losses.Count >= 2, $"{losses.Count} écarts positifs");
        Assert.All(variance.Rows, r => Assert.NotNull(r.VarianceValue));

        // Les trois produits à pertes inexpliquées dominent le tableau, avec des montants plausibles pour un bistrot (quelques dizaines d'euros).
        var top3 = variance.Rows.OrderByDescending(r => r.VarianceValue).Take(3).ToList();
        Assert.Equal(
            new[] { "Beurre doux", "Crevettes grises décortiquées", "Crème fraîche liquide 35 %" }.Order(StringComparer.Ordinal),
            top3.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(top3, r => Assert.InRange(r.VarianceValue!.Value, 5m, 200m));
        Assert.All(top3, r => Assert.True(r.ActualUse > r.TheoreticalUse));

        // Le total reste modeste devant le chiffre d'affaires : une perte inexpliquée de l'ordre d'un pour cent, pas un trou.
        Assert.InRange(variance.TotalVarianceValue, 20m, 0.05m * stats.RevenueExclVat);
        Assert.Empty(variance.Warnings);
        // Les herbes ne sont pas comptées : elles sont nommées à part, avec tout ce qui a bougé sans être compté aux deux inventaires.
        Assert.Equal(["Laurier séché", "Persil plat", "Thym frais"], variance.NotCountedIngredients.Order(StringComparer.Ordinal));
        // Elles sont utilisées (et achetées) : le food cost réel ne les compte pas, il est marqué partiel.
        Assert.True(stats.ActualFoodCostPartial);
    }

    [Fact]
    public async Task Seed_EmptyDatabase_ClosesTheCountsAtTheEndOfTheirDay()
    {
        var before = DateTime.UtcNow;
        var (options, restaurantId, _) = await SeedAndReadLastFourWeeksAsync();
        var after = DateTime.UtcNow;
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var counts = await db.StockCounts.OrderBy(c => c.Date).ToListAsync();

        // Un inventaire se compte en fin de journée, après le service et les livraisons : celui d'il y a 28 jours est clôturé à 23 h 30 son jour.
        Assert.Equal(Today.AddDays(-28).AddHours(23).AddMinutes(30), counts[0].ClosedAt);

        // Celui d'aujourd'hui l'est à 23 h 30 aussi, ou à l'instant du démarrage si la soirée n'est pas finie (jamais dans le futur).
        var endOfToday = Today.AddHours(23).AddMinutes(30);
        Assert.InRange(counts[1].ClosedAt!.Value, Min(endOfToday, before.AddMinutes(-1)), Min(endOfToday, after.AddMinutes(-1)));
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    [Fact]
    public async Task Seed_EmptyDatabase_ClosesTwoCountsOfTheSameIngredients_BracketingAppliedMetroReceipts()
    {
        var (options, restaurantId, _) = await SeedAndReadLastFourWeeksAsync();
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var counts = await db.StockCounts.Include(c => c.Lines).OrderBy(c => c.Date).ToListAsync();
        var ingredients = await db.Ingredients.ToDictionaryAsync(i => i.Id);

        Assert.Equal(2, counts.Count);
        Assert.Equal([Today.AddDays(-28), Today], counts.Select(c => c.Date));
        Assert.All(counts, c =>
        {
            Assert.Equal(StockCountStatus.Cloture, c.Status);
            Assert.NotNull(c.ClosedAt);
            Assert.True(c.ClosedAt <= DateTime.UtcNow);
            Assert.True(c.Lines.Count >= 20);
            Assert.All(c.Lines, l =>
            {
                var ingredient = ingredients[l.IngredientId];
                Assert.True(l.Quantity >= 0m);
                Assert.Equal(UnitConverter.ToBase(l.Quantity, l.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams).Value, l.BaseQuantity);
            });
        });
        // Les deux inventaires comptent les mêmes ingrédients : seuls ceux-là ont une ligne d'écart.
        Assert.Equal(
            counts[0].Lines.Select(l => l.IngredientId).Order(),
            counts[1].Lines.Select(l => l.IngredientId).Order());

        var metro = await db.Suppliers.SingleAsync(s => s.Name == "Metro");
        var receipts = await db.Receipts.Include(r => r.Lines).ToListAsync();
        var metroReceipts = receipts.Where(r => r.SupplierId == metro.Id).ToList();
        Assert.NotEmpty(metroReceipts);
        Assert.All(receipts, r =>
        {
            Assert.Equal(ReceiptStatus.Applied, r.Status);
            Assert.NotNull(r.SupplierId);
            Assert.NotNull(r.ReceiptDate);
            Assert.True(r.ReceiptDate > counts[0].Date && r.ReceiptDate <= counts[1].Date);     // dans ]ouverture, clôture]
            Assert.False(r.AmountsIncludeVat);
            Assert.Equal(r.Lines.Sum(l => l.LineTotal), r.Total);
            Assert.NotEmpty(r.Lines);
            Assert.All(r.Lines, l =>
            {
                var ingredient = ingredients[l.IngredientId!.Value];
                Assert.False(l.IsIgnored);
                Assert.True(l.Quantity > 0m);
                Assert.NotNull(l.Unit);
                Assert.True(l.LineTotalExclVat > 0m);
                // Quantité convertie par l'unité de la ligne, comme à l'application d'un ticket ; le prix de ligne est le total HT divisé par cette quantité.
                var baseQuantity = UnitConverter.ToBase(l.Quantity!.Value, l.Unit!.Value, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
                Assert.True(baseQuantity.Success);
                Assert.Equal(l.LineTotalExclVat!.Value / baseQuantity.Value, l.AppliedPricePerBaseUnit!.Value, 6);
            });
        });

        // Le prix de chaque ligne est aussi dans l'historique, au jour du ticket, avec sa source.
        var receiptPrices = await db.IngredientPrices.Where(p => p.Source == PriceSource.Receipt && p.SupplierId == metro.Id).ToListAsync();
        Assert.NotEmpty(receiptPrices);
        Assert.All(receiptPrices, p => Assert.Contains(metroReceipts, r => r.ReceiptDate == p.Date && r.Lines.Any(l => l.IngredientId == p.IngredientId)));
    }

    [Fact]
    public async Task Seed_DeletingTheBistro_TakesTheWholeActivityWithIt_SoAFailedSeedLeavesNothingBehind()
    {
        // La compensation d'un seed qui échoue supprime le restaurant : tout ce que l'activité a déjà écrit doit partir avec lui.
        var options = await postgres.CreateDatabaseAsync();
        await using var provider = BuildHost(options);
        await SeedAsync(provider);
        var restaurantId = await GetBistroIdAsync(options);

        await using var global = new ChefOsDbContext(options, new FixedTenantContext(null));
        await global.Restaurants.Where(r => r.Id == restaurantId).ExecuteDeleteAsync();

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        Assert.Empty(await db.Suppliers.ToListAsync());
        Assert.Empty(await db.SalesEntries.ToListAsync());
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.OrderLines.ToListAsync());
        Assert.Empty(await db.WasteEntries.ToListAsync());
        Assert.Empty(await db.StockCounts.ToListAsync());
        Assert.Empty(await db.StockCountLines.ToListAsync());
        Assert.Empty(await db.Receipts.ToListAsync());
        Assert.Empty(await db.ReceiptLines.ToListAsync());
        Assert.Empty(await db.IngredientPrices.ToListAsync());
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
