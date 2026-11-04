using ChefOS.Back.Components.Pages.Inventory;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class InventorySheetFormTests
{
    private const int Carrots = 1;     // Légumes, grammes
    private const int Shallots = 2;    // Légumes, grammes
    private const int Cream = 3;       // Crèmerie, millilitres
    private const int Eggs = 4;        // Crèmerie, pièces

    private static readonly DateTime Day = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static StockCountSheetLine Line(int id, string name, string category, BaseUnit baseUnit, decimal? quantity = null, MeasureUnit? unit = null) =>
        new(id, name, category, baseUnit, quantity, unit);

    private static StockCountSheet Sheet(StockCountStatus status, params StockCountSheetLine[] lines) =>
        new(7, Day, status, "Fin de mois", lines);

    private static StockCountSheet DraftSheet() => Sheet(
        StockCountStatus.Brouillon,
        Line(Cream, "Crème", "Crèmerie", BaseUnit.Milliliter),
        Line(Eggs, "Œufs", "Crèmerie", BaseUnit.Piece, 30m, MeasureUnit.Piece),
        Line(Carrots, "Carottes", "Légumes", BaseUnit.Gram, 2.5m, MeasureUnit.Kg),
        Line(Shallots, "Échalotes", "Légumes", BaseUnit.Gram));

    private static InventoryLine LineOf(InventorySheetForm form, int ingredientId) =>
        form.Lines.Single(l => l.IngredientId == ingredientId);

    // ── From ─────────────────────────────────────────────────────────────────

    [Fact]
    public void From_CopiesTheHeader()
    {
        var form = InventorySheetForm.From(DraftSheet());

        Assert.Equal(7, form.Id);
        Assert.Equal(Day, form.Date);
        Assert.Equal("Fin de mois", form.Note);
        Assert.False(form.IsClosed);
    }

    [Fact]
    public void From_AClosedSheet_IsClosed() =>
        Assert.True(InventorySheetForm.From(Sheet(StockCountStatus.Cloture)).IsClosed);

    [Fact]
    public void From_KeepsTheStoredQuantityAndUnit_OfACountedLine()
    {
        var carrots = LineOf(InventorySheetForm.From(DraftSheet()), Carrots);

        Assert.Equal(2.5m, carrots.Quantity);
        Assert.Equal(MeasureUnit.Kg, carrots.Unit);
        Assert.True(carrots.IsCounted);
    }

    [Theory]
    [InlineData(BaseUnit.Gram, MeasureUnit.Kg)]
    [InlineData(BaseUnit.Milliliter, MeasureUnit.L)]
    [InlineData(BaseUnit.Piece, MeasureUnit.Piece)]
    public void From_AnUncountedLine_StartsInTheDefaultUnitOfItsBaseUnit(BaseUnit baseUnit, MeasureUnit expected)
    {
        var form = InventorySheetForm.From(Sheet(StockCountStatus.Brouillon, Line(9, "Produit", "Divers", baseUnit)));

        var line = Assert.Single(form.Lines);
        Assert.Null(line.Quantity);
        Assert.False(line.IsCounted);
        Assert.Equal(expected, line.Unit);
    }

    [Fact]
    public void Counts_TellHowManyLinesHaveAQuantity()
    {
        var form = InventorySheetForm.From(DraftSheet());

        Assert.Equal(4, form.TotalCount);
        Assert.Equal(2, form.CountedCount);

        LineOf(form, Cream).Quantity = 0m;   // zéro est un vrai comptage

        Assert.Equal(3, form.CountedCount);
    }

    // ── Groupes par catégorie ────────────────────────────────────────────────

    [Fact]
    public void Groups_GatherTheLinesByCategory_InTheOrderOfTheSheet()
    {
        var groups = InventorySheetForm.From(DraftSheet()).Groups();

        Assert.Equal(["Crèmerie", "Légumes"], groups.Select(g => g.Category));
        Assert.Equal(["Crème", "Œufs"], groups[0].Lines.Select(l => l.Name));
        Assert.Equal(["Carottes", "Échalotes"], groups[1].Lines.Select(l => l.Name));
    }

    [Fact]
    public void Groups_FilterByNameWithoutCaseOrAccents_AndDropEmptyCategories()
    {
        var groups = InventorySheetForm.From(DraftSheet()).Groups("echalote");

        var group = Assert.Single(groups);
        Assert.Equal("Légumes", group.Category);
        Assert.Equal("Échalotes", Assert.Single(group.Lines).Name);
    }

    [Fact]
    public void Groups_AFilterMatchingACategoryName_KeepsAllItsLines()
    {
        var groups = InventorySheetForm.From(DraftSheet()).Groups("cremerie");

        var group = Assert.Single(groups);
        Assert.Equal(["Crème", "Œufs"], group.Lines.Select(l => l.Name));
    }

    [Fact]
    public void Groups_CountedOnly_KeepsTheCountedLines()
    {
        var groups = InventorySheetForm.From(DraftSheet()).Groups(countedOnly: true);

        Assert.Equal(["Œufs", "Carottes"], groups.SelectMany(g => g.Lines).Select(l => l.Name));
    }

    [Fact]
    public void Groups_OfAnEmptySheet_AreEmpty() =>
        Assert.Empty(InventorySheetForm.From(Sheet(StockCountStatus.Brouillon)).Groups());

    // ── Enregistrement : l'ensemble complet ──────────────────────────────────

    [Fact]
    public void ToInputs_SendsEveryCountedLine_NotOnlyTheEditedOnes()
    {
        var form = InventorySheetForm.From(DraftSheet());

        LineOf(form, Cream).Quantity = 1.5m;
        LineOf(form, Cream).Unit = MeasureUnit.L;

        var inputs = form.ToInputs();

        Assert.Equivalent(
            new StockCountLineInput[]
            {
                new(Cream, 1.5m, MeasureUnit.L),
                new(Eggs, 30m, MeasureUnit.Piece),
                new(Carrots, 2.5m, MeasureUnit.Kg)
            },
            inputs);
    }

    [Fact]
    public void ToInputs_LeavesOutTheLinesWithoutAQuantity()
    {
        var inputs = InventorySheetForm.From(DraftSheet()).ToInputs();

        Assert.DoesNotContain(inputs, i => i.IngredientId == Shallots);
        Assert.DoesNotContain(inputs, i => i.IngredientId == Cream);
    }

    [Fact]
    public void ToInputs_ClearingAStoredQuantity_RemovesItFromTheSet()
    {
        var form = InventorySheetForm.From(DraftSheet());

        LineOf(form, Eggs).Quantity = null;

        Assert.DoesNotContain(form.ToInputs(), i => i.IngredientId == Eggs);
    }

    [Fact]
    public void ToInputs_ZeroIsARealCount()
    {
        var form = InventorySheetForm.From(DraftSheet());

        LineOf(form, Shallots).Quantity = 0m;

        Assert.Contains(form.ToInputs(), i => i == new StockCountLineInput(Shallots, 0m, MeasureUnit.Kg));
    }

    [Fact]
    public void ToInputs_OfAnUntouchedSheet_ResendsWhatIsStored() =>
        Assert.Equal(2, InventorySheetForm.From(DraftSheet()).ToInputs().Count);

    // ── Modifications ────────────────────────────────────────────────────────

    [Fact]
    public void ANewForm_IsNotDirty() =>
        Assert.False(InventorySheetForm.From(DraftSheet()).IsDirty);

    [Fact]
    public void CountingALine_MakesTheFormDirty()
    {
        var form = InventorySheetForm.From(DraftSheet());

        LineOf(form, Shallots).Quantity = 3m;

        Assert.True(form.IsDirty);
    }

    [Fact]
    public void ChangingTheUnitOfACountedLine_MakesTheFormDirty()
    {
        var form = InventorySheetForm.From(DraftSheet());

        LineOf(form, Carrots).Unit = MeasureUnit.G;

        Assert.True(form.IsDirty);
    }

    [Fact]
    public void ChangingTheUnitOfAnUncountedLine_IsNotAModification()
    {
        var form = InventorySheetForm.From(DraftSheet());

        LineOf(form, Shallots).Unit = MeasureUnit.G;

        Assert.False(form.IsDirty);
    }

    [Fact]
    public void ClearingAStoredQuantity_MakesTheFormDirty_AndPuttingItBackDoesNot()
    {
        var form = InventorySheetForm.From(DraftSheet());
        var eggs = LineOf(form, Eggs);

        eggs.Quantity = null;
        Assert.True(form.IsDirty);

        eggs.Quantity = 30m;
        Assert.False(form.IsDirty);
    }

    [Fact]
    public void CountingThenClearingAnUncountedLine_IsNotAModification()
    {
        var form = InventorySheetForm.From(DraftSheet());
        var shallots = LineOf(form, Shallots);

        shallots.Quantity = 4m;
        shallots.Quantity = null;

        Assert.False(form.IsDirty);
    }

    // ── Unités proposées ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(BaseUnit.Gram, new[] { MeasureUnit.Kg, MeasureUnit.G })]
    [InlineData(BaseUnit.Milliliter, new[] { MeasureUnit.L, MeasureUnit.Cl, MeasureUnit.Ml })]
    [InlineData(BaseUnit.Piece, new[] { MeasureUnit.Piece })]
    public void UnitChoices_AreTheUnitsOfTheBaseUnit_DefaultFirst(BaseUnit baseUnit, MeasureUnit[] expected)
    {
        var form = InventorySheetForm.From(Sheet(StockCountStatus.Brouillon, Line(9, "Produit", "Divers", baseUnit)));

        Assert.Equal(expected, form.Lines[0].UnitChoices);
    }

    [Fact]
    public void UnitChoices_KeepAStoredUnitOutsideTheUsualOnes()
    {
        // Un ingrédient en grammes compté « en pièces » (poids moyen renseigné) : la feuille doit pouvoir l'afficher.
        var form = InventorySheetForm.From(Sheet(
            StockCountStatus.Brouillon, Line(9, "Citron", "Fruits", BaseUnit.Gram, 12m, MeasureUnit.Piece)));

        Assert.Contains(MeasureUnit.Piece, form.Lines[0].UnitChoices);
        Assert.Equal(MeasureUnit.Kg, form.Lines[0].UnitChoices[0]);
    }
}
