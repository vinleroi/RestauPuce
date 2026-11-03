using System.Reflection;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Garde-fous du cloisonnement multi-restaurant au niveau du modèle EF : les types ITenantScoped sont découverts
/// depuis le modèle (pas depuis une liste écrite à la main), donc une nouvelle entité sans filtre global fait échouer ces tests.
/// </summary>
[Collection("postgres")]
public class TenantModelTests(PostgresFixture postgres)
{
    private static IEnumerable<IEntityType> TenantScopedEntityTypes(ChefOsDbContext db) =>
        db.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType));

    [Fact]
    public void EveryTenantScopedEntity_HasQueryFilter()
    {
        // Le modèle se construit sans connexion : une chaîne factice suffit.
        var options = new DbContextOptionsBuilder<ChefOsDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only").Options;
        using var db = new ChefOsDbContext(options, new FixedTenantContext(null));

        var tenantScoped = TenantScopedEntityTypes(db).ToList();

        // Garde contre un test vide : si la découverte ne trouvait rien, il passerait pour de mauvaises raisons.
        Assert.NotEmpty(tenantScoped);
        // Le filtre se déclare sur le type racine d'une hiérarchie ; les types dérivés l'héritent.
        var withoutFilter = tenantScoped
            .Where(t => t.GetRootType().GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .Order()
            .ToList();
        Assert.True(withoutFilter.Count == 0,
            "Entités ITenantScoped sans filtre global de restaurant : " + string.Join(", ", withoutFilter));
    }

    [Fact]
    public async Task NullTenant_ReadsNothing()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        await SeedEveryTenantScopedTypeAsync(options, restaurant);

        await using var owner = new ChefOsDbContext(options, new FixedTenantContext(restaurant));
        await using var noTenant = new ChefOsDbContext(options, new FixedTenantContext(null));
        var tenantScoped = TenantScopedEntityTypes(noTenant).ToList();
        Assert.NotEmpty(tenantScoped);

        foreach (var entityType in tenantScoped)
        {
            // Les données existent bien pour le restaurant... : sans ça, « 0 ligne sans tenant » ne prouverait rien.
            // Une nouvelle entité ITenantScoped doit donc être ajoutée à SeedEveryTenantScopedTypeAsync.
            Assert.True(await CountAsync(owner, entityType.ClrType) > 0,
                $"{entityType.ClrType.Name} : aucune donnée semée pour le restaurant (voir SeedEveryTenantScopedTypeAsync).");
            // ... mais invisibles quand aucun restaurant n'est courant.
            Assert.True(await CountAsync(noTenant, entityType.ClrType) == 0,
                $"{entityType.ClrType.Name} : des lignes sont lisibles sans restaurant courant.");
        }
    }

    /// <summary>Une ligne par type ITenantScoped, insérée avec le restaurant donné comme restaurant courant.</summary>
    private static async Task SeedEveryTenantScopedTypeAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));

        var supplier = new Supplier { Name = "Metro" };
        var ingredient = new Ingredient { Name = "Tomate", BaseUnit = BaseUnit.Gram, Supplier = supplier };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();

        db.IngredientPrices.Add(new IngredientPrice
        {
            IngredientId = ingredient.Id,
            Date = DateTime.UtcNow,
            PricePerBaseUnit = 0.005m,
            SupplierId = supplier.Id,
            Source = PriceSource.Manual
        });
        var recipe = new Recipe { Name = "Salade", Kind = RecipeKind.Dish };
        recipe.Lines.Add(new RecipeLine { IngredientId = ingredient.Id, Quantity = 100m, Unit = MeasureUnit.G });
        db.Recipes.Add(recipe);

        var receipt = new Receipt
        {
            SupplierId = supplier.Id,
            SupplierNameRaw = "METRO",
            Status = ReceiptStatus.Extracted,
            CreatedAt = DateTime.UtcNow
        };
        receipt.Lines.Add(new ReceiptLine { Position = 1, RawLabel = "TOMATE 1KG", IngredientId = ingredient.Id });
        db.Receipts.Add(receipt);
        db.SupplierAliases.Add(new SupplierAlias
        {
            SupplierId = supplier.Id,
            NormalizedLabel = "TOMATE 1KG",
            IngredientId = ingredient.Id
        });
        await db.SaveChangesAsync();

        db.ReceiptFiles.Add(new ReceiptFile
        {
            ReceiptId = receipt.Id,
            FileName = "ticket.jpg",
            ContentType = "image/jpeg",
            Data = [1, 2, 3]
        });

        var day = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        db.SalesEntries.Add(new SalesEntry
        {
            RecipeId = recipe.Id,
            Date = day,
            Service = ServicePeriod.Midi,
            Quantity = 12m,
            UnitPriceInclVat = 14.5m,
            VatRate = 0.12m,
            UpdatedAt = day
        });
        var order = new Order
        {
            Number = 1,
            CreatedAt = day,
            Source = OrderSource.Salle,
            Status = OrderStatus.Nouvelle,
            StatusChangedAt = day
        };
        order.Lines.Add(new OrderLine { RecipeId = recipe.Id, Quantity = 2, UnitPriceInclVat = 14.5m, VatRate = 0.12m });
        db.Orders.Add(order);
        db.WasteEntries.Add(new WasteEntry
        {
            Date = day,
            IngredientId = ingredient.Id,
            Quantity = 250m,
            Unit = MeasureUnit.G,
            Reason = WasteReason.Perime,
            CreatedAt = day
        });
        var stockCount = new StockCount { Date = day, Status = StockCountStatus.Brouillon };
        stockCount.Lines.Add(new StockCountLine
        {
            IngredientId = ingredient.Id,
            Quantity = 2m,
            Unit = MeasureUnit.Kg,
            BaseQuantity = 2000m
        });
        db.StockCounts.Add(stockCount);
        await db.SaveChangesAsync();
    }

    private static Task<int> CountAsync(ChefOsDbContext db, Type entityType) =>
        (Task<int>)CountGenericMethod.MakeGenericMethod(entityType).Invoke(null, [db])!;

    private static readonly MethodInfo CountGenericMethod =
        typeof(TenantModelTests).GetMethod(nameof(CountSetAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task<int> CountSetAsync<TEntity>(ChefOsDbContext db) where TEntity : class =>
        db.Set<TEntity>().CountAsync();
}
