using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Les journées des services suivent le fuseau du restaurant, pas celui du serveur ni l'UTC.</summary>
[Collection("postgres")]
public class RestaurantDaysTests(PostgresFixture postgres)
{
    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime Day(int year, int month, int day) => Utc(year, month, day);

    private static FakeTimeProvider ClockAt(DateTime utc) => new(new DateTimeOffset(utc));

    private static TestContextFactory Factory(DbContextOptions<ChefOsDbContext> options, int restaurantId) => new(options, restaurantId);

    /// <summary>Crée un plat à 20 € TTC (TVA 12 %) et une commande servie à l'instant de l'horloge.</summary>
    private static async Task<(int RecipeId, int OrderId)> ServedOrderAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, FakeTimeProvider clock)
    {
        await using var db = Factory(options, restaurantId).CreateDbContext();
        var recipe = new Recipe { Name = "Carbonnade", Kind = RecipeKind.Dish, SellingPriceInclVat = 20m, VatRate = 0.12m };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();

        var orders = new OrderService(Factory(options, restaurantId), clock);
        var orderId = await orders.CreateAsync(new OrderInput(OrderSource.Salle, "Table 1", null, [new OrderLineInput(recipe.Id, 1, null)]));
        await orders.AdvanceAsync(orderId, OrderStatus.Nouvelle);
        await orders.AdvanceAsync(orderId, OrderStatus.EnPreparation);
        await orders.AdvanceAsync(orderId, OrderStatus.Prete);
        return (recipe.Id, orderId);
    }

    [Fact]
    public async Task OrderCreatedAfterLocalMidnight_IsListedOnTheLocalDay()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 11, 12, 23, 30));
        var (_, orderId) = await ServedOrderAsync(options, restaurantId, clock);
        var orders = new OrderService(Factory(options, restaurantId), clock);

        Assert.Equal(OrderStatus.Servie, (await orders.GetAsync(orderId))!.Status);
        Assert.Contains(await orders.ListForDayAsync(Day(2026, 11, 13)), o => o.Id == orderId);
        Assert.DoesNotContain(await orders.ListForDayAsync(Day(2026, 11, 12)), o => o.Id == orderId);
    }

    [Fact]
    public async Task StatsCountOrderLinesOnTheLocalDay()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 11, 12, 23, 30));
        await ServedOrderAsync(options, restaurantId, clock);
        var stats = new StatsService(Factory(options, restaurantId), clock);

        var onLocalDay = await stats.GetAsync(Day(2026, 11, 13), Day(2026, 11, 13));
        var onUtcDay = await stats.GetAsync(Day(2026, 11, 12), Day(2026, 11, 12));

        Assert.Equal(17.86m, Math.Round(onLocalDay.RevenueExclVat, 2));
        Assert.Equal(0m, onUtcDay.RevenueExclVat);
    }

    [Fact]
    public async Task DashboardLastSevenDays_EndsOnLocalToday()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 11, 12, 23, 30));
        var factory = Factory(options, restaurantId);
        var dashboard = new DashboardService(new RecipeService(factory), new StatsService(factory, clock), factory, new FixedTenantContext(restaurantId), clock);

        var figures = await dashboard.GetLastSevenDaysAsync();

        Assert.Equal(Day(2026, 11, 7), figures.From);
        Assert.Equal(Day(2026, 11, 13), figures.To);
    }

    [Fact]
    public async Task WasteDatedLocalTomorrow_IsAccepted_DayAfterIsRefused()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 11, 12, 23, 30));
        var (recipeId, _) = await ServedOrderAsync(options, restaurantId, clock);
        var waste = new WasteService(Factory(options, restaurantId), clock);

        WasteInput On(DateTime day) => new(day, null, recipeId, 1m, ChefOS.Core.Units.MeasureUnit.Piece, ChefOS.Core.Sales.WasteReason.Perime, null);

        Assert.True(await waste.AddAsync(On(Day(2026, 11, 14))) > 0);
        await Assert.ThrowsAsync<ChefOS.Core.Common.DomainValidationException>(() => waste.AddAsync(On(Day(2026, 11, 15))));
    }

    [Fact]
    public async Task AutumnNightOrder_CountsOnce()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 10, 25, 1, 30));
        var (_, orderId) = await ServedOrderAsync(options, restaurantId, clock);
        var orders = new OrderService(Factory(options, restaurantId), clock);
        var stats = new StatsService(Factory(options, restaurantId), clock);

        Assert.Single(await orders.ListForDayAsync(Day(2026, 10, 25)), o => o.Id == orderId);
        Assert.DoesNotContain(await orders.ListForDayAsync(Day(2026, 10, 24)), o => o.Id == orderId);
        Assert.Equal(17.86m, Math.Round((await stats.GetAsync(Day(2026, 10, 24), Day(2026, 10, 26))).RevenueExclVat, 2));
        Assert.Equal(17.86m, Math.Round((await stats.GetAsync(Day(2026, 10, 25), Day(2026, 10, 25))).RevenueExclVat, 2));
    }

    [Fact]
    public async Task AucklandRestaurant_KeepsItsOwnDays()
    {
        var options = await postgres.CreateDatabaseAsync();
        var brussels = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var auckland = await TestData.CreateRestaurantAsync(options, "Auckland");
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
            await db.Restaurants.Where(r => r.Id == auckland).ExecuteUpdateAsync(set => set.SetProperty(r => r.TimeZoneId, "Pacific/Auckland"));
        var clock = ClockAt(Utc(2026, 11, 12, 11, 30));
        var (_, brusselsOrder) = await ServedOrderAsync(options, brussels, clock);
        var (_, aucklandOrder) = await ServedOrderAsync(options, auckland, clock);

        var brusselsDay12 = await new OrderService(Factory(options, brussels), clock).ListForDayAsync(Day(2026, 11, 12));
        var aucklandDay12 = await new OrderService(Factory(options, auckland), clock).ListForDayAsync(Day(2026, 11, 12));
        var aucklandDay13 = await new OrderService(Factory(options, auckland), clock).ListForDayAsync(Day(2026, 11, 13));

        Assert.Contains(brusselsDay12, o => o.Id == brusselsOrder);
        Assert.DoesNotContain(aucklandDay12, o => o.Id == aucklandOrder);
        Assert.Contains(aucklandDay13, o => o.Id == aucklandOrder);
    }

    /// <summary>Un ingrédient à 5 € le kg jusqu'au 01/11, puis 9 € le kg par une saisie manuelle à l'instant donné ; un plat d'1 kg, vendu les 12 et 13/11.</summary>
    private static async Task<(int IngredientId, int RecipeId)> ManualPriceScenarioAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, DateTime manualAt)
    {
        await using var db = Factory(options, restaurantId).CreateDbContext();
        var ingredient = new Ingredient { Name = "Boeuf", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = 0.009m };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();
        db.IngredientPrices.AddRange(
            new IngredientPrice { IngredientId = ingredient.Id, Date = Utc(2026, 11, 1, 12), PricePerBaseUnit = 0.005m, Source = PriceSource.Manual },
            new IngredientPrice { IngredientId = ingredient.Id, Date = manualAt, PricePerBaseUnit = 0.009m, Source = PriceSource.Manual });
        var recipe = new Recipe
        {
            Name = "Carbonnade", Kind = RecipeKind.Dish, SellingPriceInclVat = 20m, VatRate = 0.12m,
            Lines = [new RecipeLine { IngredientId = ingredient.Id, Quantity = 1000m, Unit = MeasureUnit.G }]
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        foreach (var day in new[] { Day(2026, 11, 12), Day(2026, 11, 13) })
            db.SalesEntries.Add(new SalesEntry { RecipeId = recipe.Id, Date = day, Service = ServicePeriod.Midi, Quantity = 1m, UnitPriceInclVat = 20m, VatRate = 0.12m, UpdatedAt = Utc(2026, 11, 12) });
        await db.SaveChangesAsync();
        return (ingredient.Id, recipe.Id);
    }

    [Fact]
    public async Task ManualPriceSetAfterLocalMidnight_AppliesFromTheLocalDay()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 11, 12, 23, 30));
        var (ingredientId, _) = await ManualPriceScenarioAsync(options, restaurantId, Utc(2026, 11, 12, 23, 30));
        var stats = new StatsService(Factory(options, restaurantId), clock);
        var waste = new WasteService(Factory(options, restaurantId), clock);

        // Statistiques : le coût du plat d'1 kg au prix en vigueur à la fin de la période.
        Assert.Equal(5m, Math.Round((await stats.GetAsync(Day(2026, 11, 12), Day(2026, 11, 12))).TheoreticalCost, 4));
        Assert.Equal(9m, Math.Round((await stats.GetAsync(Day(2026, 11, 13), Day(2026, 11, 13))).TheoreticalCost, 4));

        // Pertes : 1 kg jeté, valorisé au prix du jour de la perte.
        foreach (var day in new[] { Day(2026, 11, 12), Day(2026, 11, 13) })
            await waste.AddAsync(new WasteInput(day, ingredientId, null, 1000m, MeasureUnit.G, WasteReason.Perime, null));
        var items = await waste.ListAsync(Day(2026, 11, 12), Day(2026, 11, 13));
        Assert.Equal(5m, Math.Round(items.Single(i => i.Date == Day(2026, 11, 12)).Cost!.Value, 4));
        Assert.Equal(9m, Math.Round(items.Single(i => i.Date == Day(2026, 11, 13)).Cost!.Value, 4));
    }

    [Fact]
    public async Task UndatedTicket_PriceEntryIsTheLocalDayLabel()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurantId = await TestData.CreateRestaurantAsync(options, "Bruxelles");
        var clock = ClockAt(Utc(2026, 11, 12, 23, 30));
        int ingredientId, receiptId;
        await using (var db = Factory(options, restaurantId).CreateDbContext())
        {
            var ingredient = new Ingredient { Name = "Sel", BaseUnit = BaseUnit.Gram };
            db.Ingredients.Add(ingredient);
            await db.SaveChangesAsync();
            var receipt = new Receipt
            {
                Status = ReceiptStatus.Extracted,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                Lines = [new ReceiptLine { Position = 0, RawLabel = "Sel", IngredientId = ingredient.Id, Quantity = 1m, Unit = MeasureUnit.Kg, LineTotalExclVat = 2m }]
            };
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            (ingredientId, receiptId) = (ingredient.Id, receipt.Id);
        }

        var service = new ReceiptService(Factory(options, restaurantId), new ThrowingReader(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ReceiptService>.Instance, clock);
        await service.ApplyAsync(receiptId);

        await using var check = Factory(options, restaurantId).CreateDbContext();
        var entry = await check.IngredientPrices.SingleAsync(p => p.IngredientId == ingredientId);
        Assert.Equal(PriceSource.Receipt, entry.Source);
        Assert.Equal(Utc(2026, 11, 13), entry.Date);
        Assert.Equal(DateTimeKind.Utc, entry.Date.Kind);
    }

    private sealed class ThrowingReader : IReceiptReader
    {
        public Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
