using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Jetons de version (<c>xmin</c>) des commandes et des tickets : une modification croisée est refusée au lieu d'écraser l'autre.</summary>
[Collection("postgres")]
public class ConcurrencyTokenTests(PostgresFixture postgres)
{
    private const string ConcurrentMessage = "Modifié entre-temps : rechargez la page";

    private sealed class FixedReader : IReceiptReader
    {
        public Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default) =>
            Task.FromResult(new ReceiptExtraction(
                "METRO", new DateOnly(2026, 1, 15), null, false, null,
                [new ExtractedLine("Farine", 1m, MeasureUnit.Kg, null, 2m, null)]));
    }

    private static async Task<int> AddDishAsync(TestContextFactory factory, string name)
    {
        await using var db = factory.CreateDbContext();
        var recipe = new Recipe { Name = name, Kind = RecipeKind.Dish, SellingPriceInclVat = 20m, VatRate = 0.12m };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task<(OrderService Orders, int OrderId, int Dish)> ArrangeOrderAsync()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var factory = new TestContextFactory(options, restaurant);
        var orders = new OrderService(factory);
        var dish = await AddDishAsync(factory, "Tartare");
        var id = await orders.CreateAsync(new OrderInput(OrderSource.Salle, "Table 1", null, [new OrderLineInput(dish, 1, null)]));
        return (orders, id, dish);
    }

    private async Task<(ReceiptService Receipts, int ReceiptId)> ArrangeReceiptAsync()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var receipts = new ReceiptService(new TestContextFactory(options, restaurant), new FixedReader(), NullLogger<ReceiptService>.Instance);
        var id = await receipts.CreateAsync([1, 2, 3], "ticket.jpg", "image/jpeg");
        await receipts.ExtractAsync(id);
        return (receipts, id);
    }

    private static ReceiptDraftInput DraftOf(Receipt receipt, string? invoice = null) => new(
        receipt.Id, receipt.SupplierId, receipt.ReceiptDate, invoice ?? receipt.InvoiceNumber,
        receipt.Lines.Select(l => new ReceiptLineInput(l.Id, l.RawLabel, l.Quantity, l.Unit, l.LineTotalExclVat, l.VatRate, l.IngredientId, l.IsIgnored)).ToList(),
        receipt.Version);

    [Fact]
    public async Task OrderEditedInTwoTabs_SecondSaveIsRefused()
    {
        var (orders, id, dish) = await ArrangeOrderAsync();
        var tabA = (await orders.GetAsync(id))!;
        var tabB = (await orders.GetAsync(id))!;

        await orders.UpdateLinesAsync(id, [new OrderLineInput(dish, 2, null)], tabA.Version);
        var error = await Assert.ThrowsAsync<ConcurrentEditException>(
            () => orders.UpdateLinesAsync(id, [new OrderLineInput(dish, 5, null)], tabB.Version));

        Assert.Equal(ConcurrentMessage, error.Message);
        var kept = (await orders.GetAsync(id))!;
        Assert.Equal(2, Assert.Single(kept.Lines).Quantity);
        Assert.NotEqual(tabA.Version, kept.Version);
    }

    [Fact]
    public async Task OrderEdited_WithCurrentVersion_IsAccepted()
    {
        var (orders, id, dish) = await ArrangeOrderAsync();
        var read = (await orders.GetAsync(id))!;

        await orders.UpdateLinesAsync(id, [new OrderLineInput(dish, 3, null)], read.Version);

        Assert.Equal(3, Assert.Single((await orders.GetAsync(id))!.Lines).Quantity);
    }

    [Fact]
    public async Task OrderAdvancedAfterRead_EditIsRefused()
    {
        var (orders, id, dish) = await ArrangeOrderAsync();
        var read = (await orders.GetAsync(id))!;
        await orders.AdvanceAsync(id, OrderStatus.Nouvelle);

        await Assert.ThrowsAsync<ConcurrentEditException>(
            () => orders.UpdateLinesAsync(id, [new OrderLineInput(dish, 4, null)], read.Version));
    }

    [Fact]
    public async Task ReceiptDraftSavedAfterValidation_IsRefused()
    {
        var (receipts, id) = await ArrangeReceiptAsync();
        var stale = await receipts.GetAsync(id);

        // Un autre onglet modifie l'en-tête : la version lue par le premier n'est plus la bonne.
        await receipts.SaveDraftAsync(DraftOf(stale!, "F-1"));
        var error = await Assert.ThrowsAsync<ConcurrentEditException>(() => receipts.SaveDraftAsync(DraftOf(stale!, "F-2")));

        Assert.Equal(ConcurrentMessage, error.Message);
        Assert.Equal("F-1", (await receipts.GetAsync(id))!.InvoiceNumber);
    }

    [Fact]
    public async Task ReceiptDraftSavedWithCurrentVersion_IsAccepted()
    {
        var (receipts, id) = await ArrangeReceiptAsync();

        await receipts.SaveDraftAsync(DraftOf((await receipts.GetAsync(id))!, "F-1"));
        await receipts.SaveDraftAsync(DraftOf((await receipts.GetAsync(id))!, "F-2"));

        Assert.Equal("F-2", (await receipts.GetAsync(id))!.InvoiceNumber);
    }

    [Fact]
    public async Task ReceiptDeletedWhileEdited_IsRefusedCleanly()
    {
        var (receipts, id) = await ArrangeReceiptAsync();
        var read = (await receipts.GetAsync(id))!;

        await receipts.DeleteAsync(id);

        await Assert.ThrowsAsync<DomainValidationException>(() => receipts.SaveDraftAsync(DraftOf(read, "F-1")));
        await Assert.ThrowsAsync<DomainValidationException>(() => receipts.DeleteAsync(id, read.Version));
        Assert.Null(await receipts.GetAsync(id));
    }

    [Fact]
    public async Task ReceiptEditedAfterApplyingElsewhere_IsRefusedCleanly()
    {
        var (receipts, id) = await ArrangeReceiptAsync();
        var read = (await receipts.GetAsync(id))!;

        // Même version lue, mais l'autre onglet a déjà modifié le ticket.
        await receipts.SaveDraftAsync(DraftOf(read, "F-1"));
        await Assert.ThrowsAsync<ConcurrentEditException>(() => receipts.DeleteAsync(id, read.Version));
        Assert.NotNull(await receipts.GetAsync(id));
    }
}
