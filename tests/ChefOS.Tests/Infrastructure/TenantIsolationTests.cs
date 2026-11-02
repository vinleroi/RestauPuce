using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class TenantIsolationTests(PostgresFixture postgres)
{
    private static async Task<int> AddIngredientAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, string name)
    {
        await using var db = new TestContextFactory(options, restaurantId).CreateDbContext();
        var ingredient = new Ingredient { Name = name, BaseUnit = BaseUnit.Gram };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();
        return ingredient.Id;
    }

    [Fact]
    public async Task Query_OnlyReturnsCurrentRestaurantRows()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        await AddIngredientAsync(options, a, "Tomate");
        await AddIngredientAsync(options, b, "Poivron");

        await using var dbA = new TestContextFactory(options, a).CreateDbContext();
        await using var dbB = new TestContextFactory(options, b).CreateDbContext();

        Assert.Equal(["Tomate"], await dbA.Ingredients.Select(i => i.Name).ToListAsync());
        Assert.Equal(["Poivron"], await dbB.Ingredients.Select(i => i.Name).ToListAsync());
    }

    [Fact]
    public async Task FindById_OtherRestaurant_ReturnsNull()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var poivronId = await AddIngredientAsync(options, b, "Poivron");

        await using var dbA = new TestContextFactory(options, a).CreateDbContext();

        // FindAsync ne passe pas par le filtre global : on interroge via FirstOrDefaultAsync.
        Assert.Null(await dbA.Ingredients.FirstOrDefaultAsync(i => i.Id == poivronId));
    }

    [Fact]
    public async Task Add_StampsRestaurantId()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var ingredient = new Ingredient { Name = "Tomate", BaseUnit = BaseUnit.Gram };
        Assert.Equal(0, ingredient.RestaurantId);

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            db.Ingredients.Add(ingredient);
            await db.SaveChangesAsync();
        }

        Assert.Equal(a, ingredient.RestaurantId);
        await using var check = new TestContextFactory(options, a).CreateDbContext();
        Assert.Equal(a, (await check.Ingredients.SingleAsync()).RestaurantId);
    }

    [Fact]
    public async Task Add_WithoutTenant_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var db = new TestContextFactory(options, null).CreateDbContext();
        db.Ingredients.Add(new Ingredient { Name = "Tomate", BaseUnit = BaseUnit.Gram });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Add_WithOtherRestaurantId_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        await using var dbA = new TestContextFactory(options, a).CreateDbContext();
        dbA.Ingredients.Add(new Ingredient { Name = "Poivron", BaseUnit = BaseUnit.Gram, RestaurantId = b });

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbA.SaveChangesAsync());
    }

    [Fact]
    public async Task Modify_OtherRestaurantEntity_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var poivronId = await AddIngredientAsync(options, b, "Poivron");

        Ingredient poivron;
        await using (var dbB = new TestContextFactory(options, b).CreateDbContext())
            poivron = await dbB.Ingredients.AsNoTracking().SingleAsync(i => i.Id == poivronId);

        await using var dbA = new TestContextFactory(options, a).CreateDbContext();
        dbA.Ingredients.Attach(poivron);
        poivron.Name = "Piment";

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbA.SaveChangesAsync());
    }

    [Fact]
    public async Task Delete_OtherRestaurantEntity_Throws()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var poivronId = await AddIngredientAsync(options, b, "Poivron");

        Ingredient poivron;
        await using (var dbB = new TestContextFactory(options, b).CreateDbContext())
            poivron = await dbB.Ingredients.AsNoTracking().SingleAsync(i => i.Id == poivronId);

        await using var dbA = new TestContextFactory(options, a).CreateDbContext();
        dbA.Ingredients.Remove(poivron);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbA.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_Synchronous_AppliesSameRules()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");

        using (var db = new TestContextFactory(options, null).CreateDbContext())
        {
            db.Ingredients.Add(new Ingredient { Name = "Tomate", BaseUnit = BaseUnit.Gram });
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }

        using var dbA = new TestContextFactory(options, a).CreateDbContext();
        var ingredient = new Ingredient { Name = "Tomate", BaseUnit = BaseUnit.Gram };
        dbA.Ingredients.Add(ingredient);
        dbA.SaveChanges();
        Assert.Equal(a, ingredient.RestaurantId);
    }

    [Fact]
    public async Task RecipeLine_BothSources_RejectedByDatabase()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");

        await using var db = new TestContextFactory(options, a).CreateDbContext();
        var sauce = new Recipe { Name = "Sauce", Kind = RecipeKind.Preparation };
        var plat = new Recipe { Name = "Pâtes", Kind = RecipeKind.Dish };
        db.Recipes.AddRange(sauce, plat);
        await db.SaveChangesAsync();

        // Les deux clés étrangères existent : seule la contrainte CHECK peut rejeter la ligne.
        plat.Lines.Add(new RecipeLine
        {
            IngredientId = tomateId,
            SubRecipeId = sauce.Id,
            Quantity = 100m,
            Unit = MeasureUnit.G
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("CK_RecipeLine_OneSource", pg.ConstraintName);
    }
}
