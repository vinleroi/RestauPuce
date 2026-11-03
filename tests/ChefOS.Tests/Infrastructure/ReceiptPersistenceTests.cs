using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Mapping des tickets fournisseurs : précisions, enums en texte, contraintes d'unicité et comportements de suppression.</summary>
[Collection("postgres")]
public class ReceiptPersistenceTests(PostgresFixture postgres)
{
    private static readonly DateTime ReceiptDay = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<int> AddSupplierAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, string name)
    {
        await using var db = new TestContextFactory(options, restaurantId).CreateDbContext();
        var supplier = new Supplier { Name = name };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<int> AddIngredientAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, string name, int? supplierId = null)
    {
        await using var db = new TestContextFactory(options, restaurantId).CreateDbContext();
        var ingredient = new Ingredient { Name = name, BaseUnit = BaseUnit.Gram, SupplierId = supplierId };
        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();
        return ingredient.Id;
    }

    private static async Task<int> AddReceiptWithFileAsync(
        DbContextOptions<ChefOsDbContext> options, int restaurantId, int? supplierId, int? ingredientId)
    {
        await using var db = new TestContextFactory(options, restaurantId).CreateDbContext();
        var receipt = new Receipt
        {
            SupplierId = supplierId,
            SupplierNameRaw = "METRO",
            Status = ReceiptStatus.Extracted,
            CreatedAt = ReceiptDay
        };
        receipt.Lines.Add(new ReceiptLine { Position = 1, RawLabel = "TOMATE 1KG", IngredientId = ingredientId });
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();

        db.ReceiptFiles.Add(new ReceiptFile
        {
            ReceiptId = receipt.Id,
            FileName = "ticket.jpg",
            ContentType = "image/jpeg",
            Data = [1, 2, 3]
        });
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    [Fact]
    public async Task SupplierAlias_DuplicateKeyWithNullSupplier_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var beurreId = await AddIngredientAsync(options, a, "Beurre");

        await using var db = new TestContextFactory(options, a).CreateDbContext();
        db.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "BEURRE", IngredientId = beurreId });
        await db.SaveChangesAsync();

        // Sans NULLS NOT DISTINCT, PostgreSQL accepterait ce doublon : deux SupplierId nuls y sont « différents ».
        db.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "BEURRE", IngredientId = beurreId });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("IX_SupplierAliases_Key", pg.ConstraintName);
    }

    [Fact]
    public async Task SupplierAlias_SameLabelForOtherSupplierOrRestaurant_Accepted()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var metroA = await AddSupplierAsync(options, a, "Metro");
        var beurreA = await AddIngredientAsync(options, a, "Beurre");
        var beurreB = await AddIngredientAsync(options, b, "Beurre");

        await using (var dbA = new TestContextFactory(options, a).CreateDbContext())
        {
            dbA.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "BEURRE", IngredientId = beurreA });
            dbA.SupplierAliases.Add(new SupplierAlias { SupplierId = metroA, NormalizedLabel = "BEURRE", IngredientId = beurreA });
            dbA.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "BEURRE DOUX", IngredientId = beurreA });
            await dbA.SaveChangesAsync();
        }

        await using var dbB = new TestContextFactory(options, b).CreateDbContext();
        dbB.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "BEURRE", IngredientId = beurreB });
        await dbB.SaveChangesAsync();

        await using var check = new TestContextFactory(options, a).CreateDbContext();
        Assert.Equal(3, await check.SupplierAliases.CountAsync());
    }

    [Fact]
    public async Task ReceiptFile_SecondFileForSameReceipt_Rejected()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var receiptId = await AddReceiptWithFileAsync(options, a, supplierId: null, ingredientId: null);

        await using var db = new TestContextFactory(options, a).CreateDbContext();
        db.ReceiptFiles.Add(new ReceiptFile { ReceiptId = receiptId, FileName = "bis.jpg", ContentType = "image/jpeg", Data = [9] });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("IX_ReceiptFiles_ReceiptId", pg.ConstraintName);
    }

    [Fact]
    public async Task Receipt_RoundTrip_KeepsEnumsAsTextAndRoundsToColumnPrecision()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var ingredientId = await AddIngredientAsync(options, a, "Tomate");

        int receiptId;
        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            var receipt = new Receipt
            {
                SupplierNameRaw = "METRO",
                ReceiptDate = ReceiptDay,
                InvoiceNumber = "F-2026-001",
                AmountsIncludeVat = true,
                Total = 12.346m,
                Status = ReceiptStatus.Applied,
                CreatedAt = ReceiptDay,
                AppliedAt = ReceiptDay.AddHours(1)
            };
            receipt.Lines.Add(new ReceiptLine
            {
                Position = 1,
                RawLabel = "TOMATE 1KG",
                Quantity = 1.23456m,
                Unit = MeasureUnit.Kg,
                UnitPrice = 1.236m,
                LineTotal = 2.346m,
                VatRate = 0.06789m,
                LineTotalExclVat = 2.226m,
                VatToCheck = true,
                IngredientId = ingredientId,
                MatchSource = MatchSource.GlobalAlias,
                IsIgnored = true,
                AppliedPricePerBaseUnit = 0.0123456m
            });
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            receiptId = receipt.Id;
        }

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync(r => r.Id == receiptId);
            Assert.Equal(ReceiptStatus.Applied, receipt.Status);
            Assert.Equal(12.35m, receipt.Total);
            Assert.Equal(ReceiptDay, receipt.ReceiptDate);
            Assert.Equal(DateTimeKind.Utc, receipt.ReceiptDate!.Value.Kind);

            var line = Assert.Single(receipt.Lines);
            Assert.Equal(MeasureUnit.Kg, line.Unit);
            Assert.Equal(MatchSource.GlobalAlias, line.MatchSource);
            Assert.Equal(1.2346m, line.Quantity);
            Assert.Equal(1.24m, line.UnitPrice);
            Assert.Equal(2.35m, line.LineTotal);
            Assert.Equal(0.0679m, line.VatRate);
            Assert.Equal(2.23m, line.LineTotalExclVat);
            Assert.Equal(0.012346m, line.AppliedPricePerBaseUnit);
        }

        // Les enums sont stockés en texte (pas en entier).
        await using var raw = new ChefOsDbContext(options, new FixedTenantContext(null));
        var stored = await raw.Database
            .SqlQuery<string>($"""SELECT "Status" || '/' || "Unit" || '/' || "MatchSource" AS "Value" FROM "ReceiptLines" l JOIN "Receipts" r ON r."Id" = l."ReceiptId" """)
            .SingleAsync();
        Assert.Equal("Applied/Kg/GlobalAlias", stored);
    }

    [Fact]
    public async Task Receipt_NullableFields_RoundTripAsNull()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");

        int receiptId;
        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            var receipt = new Receipt { Status = ReceiptStatus.Pending, CreatedAt = ReceiptDay };
            receipt.Lines.Add(new ReceiptLine { Position = 1, RawLabel = "???" });
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            receiptId = receipt.Id;
        }

        await using var check = new TestContextFactory(options, a).CreateDbContext();
        var loaded = await check.Receipts.Include(r => r.Lines).SingleAsync(r => r.Id == receiptId);
        Assert.Null(loaded.SupplierId);
        Assert.Null(loaded.ReceiptDate);
        Assert.Null(loaded.Total);
        var line = Assert.Single(loaded.Lines);
        Assert.Null(line.Quantity);
        Assert.Null(line.Unit);
        Assert.Null(line.MatchSource);
        Assert.Null(line.IngredientId);
        Assert.Null(line.AppliedPricePerBaseUnit);
    }

    [Fact]
    public async Task DeleteSupplier_KeepsReceiptWithoutSupplier_AndDeletesItsAliases()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var metroId = await AddSupplierAsync(options, a, "Metro");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        var receiptId = await AddReceiptWithFileAsync(options, a, metroId, tomateId);

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = metroId, NormalizedLabel = "TOMATE 1KG", IngredientId = tomateId });
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "TOMATE", IngredientId = tomateId });
            await db.SaveChangesAsync();
        }

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
            await db.Suppliers.Where(s => s.Id == metroId).ExecuteDeleteAsync();

        await using var check = new TestContextFactory(options, a).CreateDbContext();
        var receipt = await check.Receipts.SingleAsync(r => r.Id == receiptId);
        Assert.Null(receipt.SupplierId);
        Assert.Equal("METRO", receipt.SupplierNameRaw);
        Assert.Equal(["TOMATE"], await check.SupplierAliases.Select(x => x.NormalizedLabel).ToListAsync());
    }

    [Fact]
    public async Task DeleteIngredient_KeepsReceiptLineWithoutIngredient_AndDeletesItsAliases()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        var receiptId = await AddReceiptWithFileAsync(options, a, supplierId: null, ingredientId: tomateId);

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "TOMATE", IngredientId = tomateId });
            await db.SaveChangesAsync();
        }

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
            await db.Ingredients.Where(i => i.Id == tomateId).ExecuteDeleteAsync();

        await using var check = new TestContextFactory(options, a).CreateDbContext();
        var line = await check.ReceiptLines.SingleAsync(l => l.ReceiptId == receiptId);
        Assert.Null(line.IngredientId);
        Assert.Empty(await check.SupplierAliases.ToListAsync());
    }

    [Fact]
    public async Task DeleteReceipt_DeletesItsLinesAndFile()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var receiptId = await AddReceiptWithFileAsync(options, a, supplierId: null, ingredientId: null);

        await using (var db = new TestContextFactory(options, a).CreateDbContext())
            await db.Receipts.Where(r => r.Id == receiptId).ExecuteDeleteAsync();

        await using var check = new TestContextFactory(options, a).CreateDbContext();
        Assert.Empty(await check.ReceiptLines.ToListAsync());
        Assert.Empty(await check.ReceiptFiles.ToListAsync());
    }

    [Fact]
    public async Task DeleteRestaurant_DeletesAllItsReceiptData()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var metroId = await AddSupplierAsync(options, a, "Metro");
        var tomateId = await AddIngredientAsync(options, a, "Tomate");
        await AddReceiptWithFileAsync(options, a, metroId, tomateId);
        await using (var db = new TestContextFactory(options, a).CreateDbContext())
        {
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = metroId, NormalizedLabel = "TOMATE", IngredientId = tomateId });
            await db.SaveChangesAsync();
        }

        await using var noTenant = new TestContextFactory(options, null).CreateDbContext();
        await noTenant.Restaurants.Where(r => r.Id == a).ExecuteDeleteAsync();

        // Tables qui contiennent encore des lignes (sans filtre global : requête SQL directe).
        await using var raw = new ChefOsDbContext(options, new FixedTenantContext(null));
        var remaining = await raw.Database
            .SqlQuery<string>($"""
                SELECT concat_ws(', ',
                    CASE WHEN EXISTS (SELECT 1 FROM "Receipts") THEN 'Receipts' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "ReceiptLines") THEN 'ReceiptLines' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "ReceiptFiles") THEN 'ReceiptFiles' END,
                    CASE WHEN EXISTS (SELECT 1 FROM "SupplierAliases") THEN 'SupplierAliases' END) AS "Value"
                """)
            .SingleAsync();
        Assert.True(remaining.Length == 0, $"Des lignes subsistent après la suppression du restaurant : {remaining}.");
    }
}
