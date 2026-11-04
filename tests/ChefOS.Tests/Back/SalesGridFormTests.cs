using ChefOS.Back.Components.Pages.Sales;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class SalesGridFormTests
{
    private const int Waterzooi = 1;   // 22,40 € TTC à 12 % : 20,00 € HT
    private const int Carbonade = 2;   // 18,00 € TTC à 6 % : 16,98 € HT (arrondi)
    private const int Soupe = 3;       // sans prix de vente

    // Le lundi 2 novembre 2026.
    private static readonly DateTime Monday = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<int, decimal> VatRates = new()
    {
        [Waterzooi] = 0.12m,
        [Carbonade] = 0.06m
    };

    private static SalesCell Day(int recipeId, int dayIndex, ServicePeriod service, decimal quantity) =>
        new(recipeId, Monday.AddDays(dayIndex), service, false, quantity);

    private static SalesCell Weekly(int recipeId, decimal quantity) => new(recipeId, Monday, ServicePeriod.Journee, true, quantity);

    private static SalesWeekDish Dish(int recipeId, string name, decimal? price, params SalesCell[] cells) => new(recipeId, name, price, cells);

    private static SalesGridForm FormWith(params SalesWeekDish[] dishes) =>
        SalesGridForm.From(new SalesWeek(Monday, dishes), VatRates);

    private static SalesGridForm TwoDishes() => FormWith(
        Dish(Waterzooi, "Waterzooi", 22.40m,
            Day(Waterzooi, 0, ServicePeriod.Journee, 10m),
            Day(Waterzooi, 4, ServicePeriod.Journee, 12m)),
        Dish(Carbonade, "Carbonade", 18m));

    // ── From ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Monday_IsTheMondayOfTheWeek_AndTheDaysFollowIt()
    {
        var form = TwoDishes();

        Assert.Equal(DayOfWeek.Monday, Monday.DayOfWeek);
        Assert.Equal(Monday, form.Monday);
        Assert.Equal(Monday, form.DateOf(0));
        Assert.Equal(Monday.AddDays(6), form.DateOf(6));
    }

    [Fact]
    public void From_BuildsOneRowPerDish_InTheOrderOfTheWeek()
    {
        var form = TwoDishes();

        Assert.Equal(["Waterzooi", "Carbonade"], form.Rows.Select(r => r.Name));
        Assert.Equal([Waterzooi, Carbonade], form.Rows.Select(r => r.RecipeId));
        Assert.Equal(22.40m, form.Rows[0].UnitPriceInclVat);
        Assert.Equal(0.12m, form.Rows[0].VatRate);
    }

    [Fact]
    public void From_PutsEachStoredQuantityInItsDayAndServiceCell()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m,
            Day(Waterzooi, 0, ServicePeriod.Midi, 4m),
            Day(Waterzooi, 0, ServicePeriod.Soir, 6m),
            Day(Waterzooi, 2, ServicePeriod.Journee, 7.5m)));
        var row = form.Rows[0];

        Assert.Equal(4m, row.Cell(0, ServicePeriod.Midi).Quantity);
        Assert.Equal(6m, row.Cell(0, ServicePeriod.Soir).Quantity);
        Assert.Equal(7.5m, row.Cell(2, ServicePeriod.Journee).Quantity);
        Assert.Null(row.Cell(0, ServicePeriod.Journee).Quantity);
        Assert.Null(row.Cell(3, ServicePeriod.Midi).Quantity);
    }

    [Fact]
    public void From_PutsTheWeeklyTotalInItsOwnCell()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m, Weekly(Waterzooi, 50m)));

        Assert.Equal(50m, form.Rows[0].WeeklyTotal.Quantity);
        Assert.True(form.Rows[0].WeeklyTotal.IsWeeklyTotal);
        Assert.Null(form.Rows[0].Cell(0, ServicePeriod.Journee).Quantity);
    }

    [Fact]
    public void From_IgnoresStoredCellsOutsideTheWeek()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m,
            new SalesCell(Waterzooi, Monday.AddDays(7), ServicePeriod.Journee, false, 9m),
            new SalesCell(Waterzooi, Monday.AddDays(-1), ServicePeriod.Journee, false, 9m)));

        Assert.Equal(0m, form.Rows[0].Total);
    }

    [Fact]
    public void From_AnEmptyWeek_HasNoRows()
    {
        var form = FormWith();

        Assert.Empty(form.Rows);
        Assert.Equal(0m, form.Total);
        Assert.Equal(0m, form.RevenueExclVat);
        Assert.False(form.IsDirty);
    }

    [Fact]
    public void HasServiceDetail_IsTrueOnlyWhenAMiddayOrEveningQuantityIsStored()
    {
        Assert.False(TwoDishes().HasServiceDetail);
        Assert.True(FormWith(Dish(Waterzooi, "Waterzooi", 22.40m, Day(Waterzooi, 1, ServicePeriod.Soir, 3m))).HasServiceDetail);
    }

    // ── Cellules modifiées ───────────────────────────────────────────────────

    [Fact]
    public void ANewForm_HasNoModifiedCell()
    {
        var form = TwoDishes();

        Assert.False(form.IsDirty);
        Assert.Empty(form.ModifiedCells());
    }

    [Fact]
    public void EditingACell_ReportsOnlyThatCell_WithItsDayAndService()
    {
        var form = TwoDishes();

        form.Rows[1].Cell(2, ServicePeriod.Soir).Quantity = 5m;

        Assert.True(form.IsDirty);
        var cell = Assert.Single(form.ModifiedCells());
        Assert.Equal(new SalesCell(Carbonade, Monday.AddDays(2), ServicePeriod.Soir, false, 5m), cell);
    }

    [Fact]
    public void ChangingAStoredCell_ReportsItsNewQuantity()
    {
        var form = TwoDishes();

        form.Rows[0].Cell(0, ServicePeriod.Journee).Quantity = 11m;

        var cell = Assert.Single(form.ModifiedCells());
        Assert.Equal(new SalesCell(Waterzooi, Monday, ServicePeriod.Journee, false, 11m), cell);
    }

    [Fact]
    public void PuttingBackTheStoredValue_UnmarksTheCell()
    {
        var form = TwoDishes();
        var cell = form.Rows[0].Cell(0, ServicePeriod.Journee);

        cell.Quantity = 11m;
        cell.Quantity = 10m;

        Assert.False(cell.IsModified);
        Assert.False(form.IsDirty);
    }

    [Fact]
    public void ClearingAStoredCell_SendsAQuantityOfZero_WhichDeletesTheEntry()
    {
        var form = TwoDishes();

        form.Rows[0].Cell(4, ServicePeriod.Journee).Quantity = null;

        var cell = Assert.Single(form.ModifiedCells());
        Assert.Equal(new SalesCell(Waterzooi, Monday.AddDays(4), ServicePeriod.Journee, false, 0m), cell);
    }

    [Fact]
    public void TypingZeroInAStoredCell_AlsoSendsAQuantityOfZero()
    {
        var form = TwoDishes();

        form.Rows[0].Cell(4, ServicePeriod.Journee).Quantity = 0m;

        Assert.Equal(0m, Assert.Single(form.ModifiedCells()).Quantity);
    }

    [Fact]
    public void AnEmptyCell_TypedAsZeroOrClearedAgain_IsNotAModification()
    {
        var form = TwoDishes();
        var cell = form.Rows[1].Cell(3, ServicePeriod.Midi);

        cell.Quantity = 0m;
        Assert.False(cell.IsModified);

        cell.Quantity = 3m;
        cell.Quantity = null;
        Assert.False(cell.IsModified);
        Assert.Empty(form.ModifiedCells());
    }

    [Fact]
    public void ADifferenceBelowTheStorageScale_IsNotAModification()
    {
        // Le service arrondit à 4 décimales : 10,00001 serait enregistré comme 10.
        var form = TwoDishes();

        form.Rows[0].Cell(0, ServicePeriod.Journee).Quantity = 10.00001m;

        Assert.False(form.IsDirty);
    }

    [Fact]
    public void EditingTheWeeklyTotal_SendsAWeeklyTotalDatedMonday_InJournee()
    {
        var form = TwoDishes();

        form.Rows[1].WeeklyTotal.Quantity = 40m;

        var cell = Assert.Single(form.ModifiedCells());
        Assert.Equal(new SalesCell(Carbonade, Monday, ServicePeriod.Journee, true, 40m), cell);
    }

    [Fact]
    public void ModifiedCells_ComeInRowOrder_WeeklyTotalLastOfItsRow()
    {
        var form = TwoDishes();

        form.Rows[1].Cell(0, ServicePeriod.Journee).Quantity = 1m;
        form.Rows[0].WeeklyTotal.Quantity = 2m;
        form.Rows[0].Cell(1, ServicePeriod.Midi).Quantity = 3m;

        var cells = form.ModifiedCells();

        Assert.Equal(
            [(Waterzooi, false), (Waterzooi, true), (Carbonade, false)],
            cells.Select(c => (c.RecipeId, c.IsWeeklyTotal)));
        Assert.Equal(3, form.ModifiedCount);
    }

    // ── Totaux ───────────────────────────────────────────────────────────────

    [Fact]
    public void DayTotal_AddsTheThreeServices()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m,
            Day(Waterzooi, 1, ServicePeriod.Journee, 2m),
            Day(Waterzooi, 1, ServicePeriod.Midi, 3m),
            Day(Waterzooi, 1, ServicePeriod.Soir, 4m)));

        Assert.Equal(9m, form.Rows[0].DayTotal(1));
        Assert.Equal(0m, form.Rows[0].DayTotal(0));
    }

    [Fact]
    public void ServiceTotal_AddsTheDaysOfOneService()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m,
            Day(Waterzooi, 0, ServicePeriod.Midi, 3m),
            Day(Waterzooi, 5, ServicePeriod.Midi, 4m),
            Day(Waterzooi, 5, ServicePeriod.Soir, 9m)));

        Assert.Equal(7m, form.Rows[0].ServiceTotal(ServicePeriod.Midi));
        Assert.Equal(9m, form.Rows[0].ServiceTotal(ServicePeriod.Soir));
    }

    [Fact]
    public void RowTotal_AddsEveryDayAndTheWeeklyTotal()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m,
            Day(Waterzooi, 0, ServicePeriod.Journee, 10m),
            Day(Waterzooi, 3, ServicePeriod.Soir, 2.5m),
            Weekly(Waterzooi, 30m)));

        Assert.Equal(42.5m, form.Rows[0].Total);
    }

    [Fact]
    public void TotalsFollowTheEditedCells_WithoutSaving()
    {
        var form = TwoDishes();

        form.Rows[0].Cell(0, ServicePeriod.Journee).Quantity = 15m;
        form.Rows[1].Cell(0, ServicePeriod.Journee).Quantity = 5m;

        Assert.Equal(20m, form.DayTotal(0));
        Assert.Equal(12m, form.DayTotal(4));
        Assert.Equal(0m, form.DayTotal(1));
        Assert.Equal(15m + 12m + 5m, form.Total);
    }

    [Fact]
    public void WeeklyTotalsSum_IsTheSumOfTheWeeklyTotalColumn()
    {
        var form = FormWith(
            Dish(Waterzooi, "Waterzooi", 22.40m, Weekly(Waterzooi, 30m)),
            Dish(Carbonade, "Carbonade", 18m, Weekly(Carbonade, 12m)));

        Assert.Equal(42m, form.WeeklyTotalsSum);
    }

    // ── CA HT ────────────────────────────────────────────────────────────────

    [Fact]
    public void RevenueExclVat_IsQuantityTimesPriceDividedByOnePlusVat()
    {
        var form = TwoDishes();   // 22 portions de waterzooi à 20,00 € HT

        Assert.Equal(440m, form.Rows[0].RevenueExclVat);
        Assert.Equal(440m, form.RevenueExclVat);
    }

    [Fact]
    public void RevenueExclVat_IsNotRounded()
    {
        var form = FormWith(Dish(Carbonade, "Carbonade", 18m, Day(Carbonade, 0, ServicePeriod.Journee, 3m)));

        Assert.Equal(3m * 18m / 1.06m, form.RevenueExclVat);
    }

    [Fact]
    public void RevenueExclVat_CountsTheWeeklyTotalAndEveryService()
    {
        var form = FormWith(Dish(Waterzooi, "Waterzooi", 22.40m,
            Day(Waterzooi, 0, ServicePeriod.Midi, 1m),
            Day(Waterzooi, 0, ServicePeriod.Soir, 1m),
            Weekly(Waterzooi, 8m)));

        Assert.Equal(200m, form.RevenueExclVat);
    }

    [Fact]
    public void RevenueExclVat_IsUnknownForADishWithoutPriceOrVat_AndLeftOutOfTheTotal()
    {
        var form = FormWith(
            Dish(Soupe, "Soupe du jour", null, Day(Soupe, 0, ServicePeriod.Journee, 5m)),
            Dish(Waterzooi, "Waterzooi", 22.40m, Day(Waterzooi, 0, ServicePeriod.Journee, 1m)));

        Assert.Null(form.Rows[0].RevenueExclVat);
        Assert.Equal(20m, form.RevenueExclVat);
    }

    [Fact]
    public void RevenueExclVat_FollowsTheEditedCells()
    {
        var form = TwoDishes();

        form.Rows[0].Cell(0, ServicePeriod.Journee).Quantity = 0m;

        Assert.Equal(240m, form.RevenueExclVat);
    }
}
