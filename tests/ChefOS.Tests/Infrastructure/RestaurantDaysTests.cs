using ChefOS.Core.Recipes;
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
}
