using ChefOS.Back.Components.Pages.Orders;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class OrderDraftTests
{
    private const int Waterzooi = 1;
    private const int Carbonade = 2;

    [Fact]
    public void ANewDraft_IsATableOrderWithoutLines()
    {
        var draft = OrderDraft.New();

        Assert.Equal(OrderSource.Salle, draft.Source);
        Assert.Empty(draft.Lines);
        Assert.Null(draft.EstimatedTotal);
    }

    [Fact]
    public void AddDish_AddsALineOfOnePortion()
    {
        var draft = OrderDraft.New();

        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);

        var line = Assert.Single(draft.Lines);
        Assert.Equal((Waterzooi, "Waterzooi", 1), (line.RecipeId, line.DishName, line.Quantity));
    }

    [Fact]
    public void AddDish_OfADishAlreadyInTheOrder_AddsAPortionToItsLine()
    {
        var draft = OrderDraft.New();

        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);
        draft.AddDish(Carbonade, "Carbonade", 18m);
        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);

        Assert.Equal([(Waterzooi, 2), (Carbonade, 1)], draft.Lines.Select(l => (l.RecipeId, l.Quantity!.Value)));
    }

    [Fact]
    public void Remove_DropsTheLine()
    {
        var draft = OrderDraft.New();
        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);
        draft.AddDish(Carbonade, "Carbonade", 18m);

        draft.Remove(draft.Lines[0]);

        Assert.Equal(Carbonade, Assert.Single(draft.Lines).RecipeId);
    }

    [Fact]
    public void EstimatedTotal_AddsQuantitiesTimesCurrentPrices()
    {
        var draft = OrderDraft.New();
        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);
        draft.AddDish(Carbonade, "Carbonade", 18m);
        draft.Lines[0].Quantity = 2;

        Assert.Equal(2 * 22.40m + 18m, draft.EstimatedTotal);
    }

    [Fact]
    public void ToInput_CarriesTheSourceTheCustomerTheNoteAndEveryLine()
    {
        var draft = OrderDraft.New();
        draft.Source = OrderSource.Telephone;
        draft.TableOrCustomer = "Mme Martin";
        draft.Note = "Allergie aux noix";
        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);
        draft.Lines[0].Quantity = 3;
        draft.Lines[0].Note = "sans oignon";

        var input = draft.ToInput();

        Assert.Equal((OrderSource.Telephone, "Mme Martin", "Allergie aux noix"), (input.Source, input.TableOrCustomer, input.Note));
        Assert.Equal(new OrderLineInput(Waterzooi, 3, "sans oignon"), Assert.Single(input.Lines));
    }

    [Fact]
    public void ToLineInputs_TreatsAnEmptyQuantityAsZero_SoTheServiceRefusesIt()
    {
        var draft = OrderDraft.New();
        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);
        draft.Lines[0].Quantity = null;

        Assert.Equal(0, Assert.Single(draft.ToLineInputs()).Quantity);
    }

    [Fact]
    public void From_AnExistingOrder_KeepsItsLinesWithTheirDishIds()
    {
        var summary = new OrderSummary(
            12, 5, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), OrderSource.Emporter, OrderStatus.EnPreparation, "Mme Dupont", 62.8m,
            [new OrderLineSummary(Waterzooi, "Waterzooi", 2, "saignant"), new OrderLineSummary(Carbonade, "Carbonade", 1, null)]);

        var draft = OrderDraft.From(summary);

        Assert.Equal((OrderSource.Emporter, "Mme Dupont"), (draft.Source, draft.TableOrCustomer));
        Assert.Equal(
            [(Waterzooi, "Waterzooi", 2, "saignant"), (Carbonade, "Carbonade", 1, (string?)null)],
            draft.Lines.Select(l => (l.RecipeId, l.DishName, l.Quantity!.Value, l.Note)));
    }

    [Fact]
    public void AnExistingOrderHasNoEstimatedTotal_ItsFrozenPricesAreNotKnownHere()
    {
        var summary = new OrderSummary(
            12, 5, DateTime.UtcNow, OrderSource.Salle, OrderStatus.Nouvelle, null, 62.8m,
            [new OrderLineSummary(Waterzooi, "Waterzooi", 2, null)]);

        Assert.Null(OrderDraft.From(summary).EstimatedTotal);
    }

    [Theory]
    [InlineData(OrderStatus.Nouvelle, true)]
    [InlineData(OrderStatus.EnPreparation, true)]
    [InlineData(OrderStatus.Prete, true)]
    [InlineData(OrderStatus.Servie, false)]
    [InlineData(OrderStatus.Annulee, false)]
    public void IsOpen_UntilTheOrderIsServedOrCancelled(OrderStatus status, bool expected) =>
        Assert.Equal(expected, OrderDraft.IsOpen(status));

    // ── Modifications non enregistrées ───────────────────────────────────────

    private static OrderSummary ExistingOrder() => new(
        12, 5, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), OrderSource.Emporter, OrderStatus.EnPreparation, "Mme Dupont", 62.8m,
        [new OrderLineSummary(Waterzooi, "Waterzooi", 2, "saignant"), new OrderLineSummary(Carbonade, "Carbonade", 1, null)]);

    [Fact]
    public void ANewEmptyDraft_AndAnUntouchedExistingOrder_AreNotDirty()
    {
        Assert.False(OrderDraft.New().IsDirty);
        Assert.False(OrderDraft.From(ExistingOrder()).IsDirty);
    }

    [Fact]
    public void AddingADish_MakesANewDraftDirty()
    {
        var draft = OrderDraft.New();

        draft.AddDish(Waterzooi, "Waterzooi", 22.40m);

        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void TypingTheTableTheNoteOrChangingTheSource_MakesANewDraftDirty()
    {
        var table = OrderDraft.New();
        table.TableOrCustomer = "Table 4";
        var note = OrderDraft.New();
        note.Note = "Allergie";
        var source = OrderDraft.New();
        source.Source = OrderSource.Vitrine;

        Assert.All(new[] { table, note, source }, draft => Assert.True(draft.IsDirty));
    }

    [Fact]
    public void AnEmptyOrBlankText_IsTheSameAsNoText()
    {
        var draft = OrderDraft.From(ExistingOrder());

        draft.Lines[1].Note = "   ";   // « Carbonade » n'avait pas de remarque
        draft.Note = "";

        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void ChangingALineOfAnExistingOrder_MakesItDirty_AndPuttingItBackDoesNot()
    {
        var draft = OrderDraft.From(ExistingOrder());

        draft.Lines[0].Quantity = 3;
        Assert.True(draft.IsDirty);
        draft.Lines[0].Quantity = 2;
        Assert.False(draft.IsDirty);

        draft.Lines[0].Note = "bien cuit";
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void RemovingOrAddingALine_MakesAnExistingOrderDirty()
    {
        var removed = OrderDraft.From(ExistingOrder());
        removed.Remove(removed.Lines[1]);
        var added = OrderDraft.From(ExistingOrder());
        added.AddDish(3, "Gaufre", 9.5m);

        Assert.True(removed.IsDirty);
        Assert.True(added.IsDirty);
    }
}
