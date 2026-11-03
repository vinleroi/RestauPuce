using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Mapping des ventes, commandes, pertes et inventaires : précisions, enums en texte, contraintes et comportements de suppression.</summary>
[Collection("postgres")]
public class SalesPersistenceTests(PostgresFixture postgres)
{
    private static readonly DateTime Day = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static async Task<int> AddIngredientAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, string name)
    {
        await using var db = OpenContext(options, restaurantId);
        var ingredient = new Ingredient { Name = name, BaseUnit = BaseUnit.Gram };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();
        return ingredient.Id;
    }

    private static async Task<int> AddDishAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, string name)
    {
        await using var db = OpenContext(options, restaurantId);
        var dish = new Recipe { Name = name, Kind = RecipeKind.Dish, SellingPriceInclVat = 22.40m, VatRate = 0.12m };
        db.Recipes.Add(dish);
        await db.SaveChangesAsync();
        return dish.Id;
    }

    private static SalesEntry Sale(int recipeId, ServicePeriod service = ServicePeriod.Midi, bool weekly = false, DateTime? date = null) =>
        new()
        {
            RecipeId = recipeId,
            Date = date ?? Day,
            Service = service,
            IsWeeklyTotal = weekly,
            Quantity = 10m,
            UnitPriceInclVat = 22.40m,
            VatRate = 0.12m,
            UpdatedAt = Day
        };

    private static Order NewOrder(int number, params int[] recipeIds)
    {
        var order = new Order
        {
            Number = number,
            CreatedAt = Day,
            Source = OrderSource.Salle,
            Status = OrderStatus.Nouvelle,
            StatusChangedAt = Day
        };
        foreach (var recipeId in recipeIds)
            order.Lines.Add(new OrderLine { RecipeId = recipeId, Quantity = 1, UnitPriceInclVat = 22.40m, VatRate = 0.12m });
        return order;
    }

    private static WasteEntry Waste(int? ingredientId, int? recipeId) =>
        new()
        {
            Date = Day,
            IngredientId = ingredientId,
            RecipeId = recipeId,
            Quantity = 100m,
            Unit = ingredientId is null ? MeasureUnit.Piece : MeasureUnit.G,
            Reason = WasteReason.Casse,
            CreatedAt = Day
        };

    private static StockCountLine CountLine(int ingredientId) =>
        new() { IngredientId = ingredientId, Quantity = 2m, Unit = MeasureUnit.Kg, BaseQuantity = 2000m };

    // ── Contraintes d'unicité et de cohérence ────────────────────────────────

    [Fact]
    public async Task SalesEntry_DuplicateKey_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var dishId = await AddDishAsync(options, a, "Carbonnade");

        await using var db = OpenContext(options, a);
        db.SalesEntries.Add(Sale(dishId));
        await db.SaveChangesAsync();

        db.SalesEntries.Add(Sale(dishId));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("IX_SalesEntries_Key", pg.ConstraintName);
    }

    [Fact]
    public async Task SalesEntry_SameDishOtherServiceDayWeeklyFlagOrRestaurant_Accepted()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var dishA = await AddDishAsync(options, a, "Carbonnade");
        var dishB = await AddDishAsync(options, b, "Carbonnade");

        await using (var db = OpenContext(options, a))
        {
            db.SalesEntries.Add(Sale(dishA, ServicePeriod.Midi));
            db.SalesEntries.Add(Sale(dishA, ServicePeriod.Soir));
            db.SalesEntries.Add(Sale(dishA, ServicePeriod.Midi, date: Day.AddDays(1)));
            db.SalesEntries.Add(Sale(dishA, ServicePeriod.Midi, weekly: true));
            await db.SaveChangesAsync();
        }

        await using (var db = OpenContext(options, b))
        {
            db.SalesEntries.Add(Sale(dishB, ServicePeriod.Midi));
            await db.SaveChangesAsync();
        }

        await using var check = OpenContext(options, a);
        Assert.Equal(4, await check.SalesEntries.CountAsync());
    }

    [Fact]
    public async Task Order_DuplicateNumberInSameRestaurant_Rejected_ButAcceptedInAnother()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");

        await using (var dbB = OpenContext(options, b))
        {
            dbB.Orders.Add(NewOrder(1));
            await dbB.SaveChangesAsync();
        }

        await using var db = OpenContext(options, a);
        db.Orders.Add(NewOrder(1));
        await db.SaveChangesAsync();

        db.Orders.Add(NewOrder(1));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("IX_Orders_Number", pg.ConstraintName);
    }

    [Fact]
    public async Task StockCountLine_SameIngredientTwiceInOneCount_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");

        await using var db = OpenContext(options, a);
        var count = new StockCount { Date = Day, Status = StockCountStatus.Brouillon };
        count.Lines.Add(CountLine(tomateId));
        db.StockCounts.Add(count);
        await db.SaveChangesAsync();

        // Le même ingrédient dans un autre inventaire reste permis.
        var other = new StockCount { Date = Day.AddDays(7), Status = StockCountStatus.Brouillon };
        other.Lines.Add(CountLine(tomateId));
        db.StockCounts.Add(other);
        await db.SaveChangesAsync();

        count.Lines.Add(CountLine(tomateId));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("IX_StockCountLines_Key", pg.ConstraintName);
    }

    [Fact]
    public async Task WasteEntry_BothTargets_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        var dishId = await AddDishAsync(options, a, "Carbonnade");

        await using var db = OpenContext(options, a);
        db.WasteEntries.Add(Waste(tomateId, dishId));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("CK_WasteEntry_OneTarget", pg.ConstraintName);
    }

    [Fact]
    public async Task WasteEntry_NoTarget_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");

        await using var db = OpenContext(options, a);
        db.WasteEntries.Add(Waste(null, null));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("CK_WasteEntry_OneTarget", pg.ConstraintName);
    }

    // ── Aller-retour : précisions et énumérations en texte ───────────────────

    [Fact]
    public async Task SalesData_RoundTrip_KeepsEnumsAsTextAndRoundsToColumnPrecision()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        var dishId = await AddDishAsync(options, a, "Carbonnade");

        int orderId;
        await using (var db = OpenContext(options, a))
        {
            var sale = Sale(dishId, ServicePeriod.Soir, weekly: true);
            sale.Quantity = 12.34567m;
            sale.UnitPriceInclVat = 22.405m;
            sale.VatRate = 0.12345m;
            db.SalesEntries.Add(sale);

            var order = NewOrder(7, dishId);
            order.Source = OrderSource.Vitrine;
            order.Status = OrderStatus.EnPreparation;
            order.TableOrCustomer = "Table 4";
            order.Lines[0].Quantity = 3;
            order.Lines[0].UnitPriceInclVat = 18.995m;
            order.Lines[0].VatRate = 0.06m;
            db.Orders.Add(order);

            var waste = Waste(tomateId, null);
            waste.Quantity = 0.12345m;
            waste.Reason = WasteReason.Surproduction;
            db.WasteEntries.Add(waste);

            var count = new StockCount { Date = Day, Status = StockCountStatus.Cloture, ClosedAt = Day.AddHours(18) };
            count.Lines.Add(new StockCountLine
            {
                IngredientId = tomateId,
                Quantity = 1.23456m,
                Unit = MeasureUnit.Kg,
                BaseQuantity = 1234.56789m
            });
            db.StockCounts.Add(count);

            await db.SaveChangesAsync();
            orderId = order.Id;
        }

        await using (var db = OpenContext(options, a))
        {
            var sale = await db.SalesEntries.SingleAsync();
            Assert.Equal(ServicePeriod.Soir, sale.Service);
            Assert.True(sale.IsWeeklyTotal);
            Assert.Equal(12.3457m, sale.Quantity);
            Assert.Equal(22.41m, sale.UnitPriceInclVat);
            Assert.Equal(0.1235m, sale.VatRate);
            Assert.Equal(Day, sale.Date);
            Assert.Equal(DateTimeKind.Utc, sale.Date.Kind);

            var order = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderSource.Vitrine, order.Source);
            Assert.Equal(OrderStatus.EnPreparation, order.Status);
            Assert.Equal("Table 4", order.TableOrCustomer);
            Assert.Null(order.Note);
            var line = Assert.Single(order.Lines);
            Assert.Equal(3, line.Quantity);
            Assert.Equal(19.00m, line.UnitPriceInclVat);
            Assert.Equal(0.06m, line.VatRate);

            var waste = await db.WasteEntries.SingleAsync();
            Assert.Equal(0.1235m, waste.Quantity);
            Assert.Equal(MeasureUnit.G, waste.Unit);
            Assert.Equal(WasteReason.Surproduction, waste.Reason);
            Assert.Null(waste.RecipeId);

            var count = await db.StockCounts.Include(c => c.Lines).SingleAsync();
            Assert.Equal(StockCountStatus.Cloture, count.Status);
            Assert.Equal(Day.AddHours(18), count.ClosedAt);
            var countLine = Assert.Single(count.Lines);
            Assert.Equal(MeasureUnit.Kg, countLine.Unit);
            Assert.Equal(1.2346m, countLine.Quantity);
            Assert.Equal(1234.5679m, countLine.BaseQuantity);
        }

        // Les enums sont stockés en texte (pas en entier).
        await using var raw = new ChefOsDbContext(options, new FixedTenantContext(null));
        var stored = await raw.Database
            .SqlQuery<string>($"""
                SELECT concat_ws('/',
                    (SELECT "Service" FROM "SalesEntries"),
                    (SELECT "Source" || '+' || "Status" FROM "Orders"),
                    (SELECT "Unit" || '+' || "Reason" FROM "WasteEntries"),
                    (SELECT c."Status" || '+' || l."Unit" FROM "StockCounts" c JOIN "StockCountLines" l ON l."StockCountId" = c."Id")) AS "Value"
                """)
            .SingleAsync();
        Assert.Equal("Soir/Vitrine+EnPreparation/G+Surproduction/Cloture+Kg", stored);
    }

    // ── Règles de suppression ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRecipe_UsedInOrder_RejectedByDatabase_ButCascadesSalesAndWaste()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var orderedDish = await AddDishAsync(options, a, "Carbonnade");
        var otherDish = await AddDishAsync(options, a, "Stoemp");
        await using (var db = OpenContext(options, a))
        {
            db.Orders.Add(NewOrder(1, orderedDish));
            db.SalesEntries.Add(Sale(otherDish));
            db.WasteEntries.Add(Waste(null, otherDish));
            await db.SaveChangesAsync();
        }

        // Filet de sécurité : une commande garde ses plats, même si le service est contourné.
        await using (var db = OpenContext(options, a))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => db.Recipes.Where(r => r.Id == orderedDish).ExecuteDeleteAsync());
            Assert.Equal("FK_OrderLines_Recipes_RecipeId", ex.ConstraintName);
        }

        await using (var db = OpenContext(options, a))
            await db.Recipes.Where(r => r.Id == otherDish).ExecuteDeleteAsync();

        await using var check = OpenContext(options, a);
        Assert.Empty(await check.SalesEntries.ToListAsync());
        Assert.Empty(await check.WasteEntries.ToListAsync());
        Assert.Single(await check.OrderLines.ToListAsync());
    }

    [Fact]
    public async Task DeleteIngredient_DeletesItsWasteAndStockCountLines()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        var oignonId = await AddIngredientAsync(options, a, "Oignon");
        await using (var db = OpenContext(options, a))
        {
            db.WasteEntries.Add(Waste(tomateId, null));
            db.WasteEntries.Add(Waste(oignonId, null));
            var count = new StockCount { Date = Day, Status = StockCountStatus.Brouillon };
            count.Lines.Add(CountLine(tomateId));
            count.Lines.Add(CountLine(oignonId));
            db.StockCounts.Add(count);
            await db.SaveChangesAsync();
        }

        await using (var db = OpenContext(options, a))
            await db.Ingredients.Where(i => i.Id == tomateId).ExecuteDeleteAsync();

        await using var check = OpenContext(options, a);
        Assert.Equal(oignonId, (await check.WasteEntries.SingleAsync()).IngredientId);
        Assert.Equal(oignonId, (await check.StockCountLines.SingleAsync()).IngredientId);
        Assert.Single(await check.StockCounts.ToListAsync());
    }

    [Fact]
    public async Task DeleteOrderAndStockCount_DeleteTheirLines()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        var dishId = await AddDishAsync(options, a, "Carbonnade");
        await using (var db = OpenContext(options, a))
        {
            db.Orders.Add(NewOrder(1, dishId, dishId));
            var count = new StockCount { Date = Day, Status = StockCountStatus.Brouillon };
            count.Lines.Add(CountLine(tomateId));
            db.StockCounts.Add(count);
            await db.SaveChangesAsync();
        }

        await using (var db = OpenContext(options, a))
        {
            await db.Orders.ExecuteDeleteAsync();
            await db.StockCounts.ExecuteDeleteAsync();
        }

        await using var check = OpenContext(options, a);
        Assert.Empty(await check.OrderLines.ToListAsync());
        Assert.Empty(await check.StockCountLines.ToListAsync());
        Assert.Single(await check.Recipes.ToListAsync());
        Assert.Single(await check.Ingredients.ToListAsync());
    }

    [Fact]
    public async Task DeleteRestaurant_CascadesSalesAndStock()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        foreach (var restaurant in new[] { a, b })
        {
            var tomateId = await AddIngredientAsync(options, restaurant, "Tomate");
            var dishId = await AddDishAsync(options, restaurant, "Carbonnade");
            await using var db = OpenContext(options, restaurant);
            db.SalesEntries.Add(Sale(dishId));
            db.Orders.Add(NewOrder(1, dishId));
            db.WasteEntries.Add(Waste(tomateId, null));
            db.WasteEntries.Add(Waste(null, dishId));
            var count = new StockCount { Date = Day, Status = StockCountStatus.Brouillon };
            count.Lines.Add(CountLine(tomateId));
            db.StockCounts.Add(count);
            await db.SaveChangesAsync();
        }

        // Les commandes référencent les plats en Restrict : supprimer le restaurant doit pourtant tout emporter d'un coup.
        await using (var noTenant = OpenContextWithoutTenant(options))
            await noTenant.Restaurants.Where(r => r.Id == a).ExecuteDeleteAsync();

        // Tables qui contiennent encore des lignes du restaurant supprimé (sans filtre global : requête SQL directe).
        await using var raw = OpenContextWithoutTenant(options);
        var remaining = await raw.Database
            .SqlQuery<string>($"""
                SELECT concat_ws(', ',
                    CASE WHEN EXISTS (SELECT 1 FROM "SalesEntries" WHERE "RestaurantId" = {a}) THEN 'SalesEntries' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "Orders" WHERE "RestaurantId" = {a}) THEN 'Orders' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "OrderLines" WHERE "RestaurantId" = {a}) THEN 'OrderLines' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "WasteEntries" WHERE "RestaurantId" = {a}) THEN 'WasteEntries' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "StockCounts" WHERE "RestaurantId" = {a}) THEN 'StockCounts' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "StockCountLines" WHERE "RestaurantId" = {a}) THEN 'StockCountLines' END) AS "Value"
                """)
            .SingleAsync();
        Assert.True(remaining.Length == 0, $"Des lignes subsistent après la suppression du restaurant : {remaining}.");

        // L'autre restaurant n'est pas touché.
        await using var other = OpenContext(options, b);
        Assert.Single(await other.SalesEntries.ToListAsync());
        Assert.Single(await other.Orders.ToListAsync());
        Assert.Single(await other.OrderLines.ToListAsync());
        Assert.Equal(2, await other.WasteEntries.CountAsync());
        Assert.Single(await other.StockCountLines.ToListAsync());
    }

    private static ChefOsDbContext OpenContextWithoutTenant(DbContextOptions<ChefOsDbContext> options) =>
        new TestContextFactory(options, null).CreateDbContext();
}
