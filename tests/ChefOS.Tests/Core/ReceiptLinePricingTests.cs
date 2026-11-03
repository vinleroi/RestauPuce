using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Units;

namespace ChefOS.Tests.Core;

public class ReceiptLinePricingTests
{
    private static Ingredient Ing(BaseUnit baseUnit, decimal? pieceWeight = null)
        => new() { Id = 1, Name = "Test", BaseUnit = baseUnit, AveragePieceWeightGrams = pieceWeight };

    [Fact]
    public void Compute_KgLine_PricePerGram()
    {
        var r = ReceiptLinePricing.Compute(2.5m, MeasureUnit.Kg, 30.00m, Ing(BaseUnit.Gram));

        Assert.Equal(ReceiptLineIssue.None, r.Issue);
        Assert.Equal(2500m, r.BaseQuantity);
        Assert.Equal(0.012m, r.PricePerBaseUnit);
    }

    [Fact]
    public void Compute_LitreLine_PricePerMl()
    {
        var r = ReceiptLinePricing.Compute(6m, MeasureUnit.L, 9.00m, Ing(BaseUnit.Milliliter));

        Assert.Equal(ReceiptLineIssue.None, r.Issue);
        Assert.Equal(6000m, r.BaseQuantity);
        Assert.Equal(0.0015m, r.PricePerBaseUnit);
    }

    [Fact]
    public void Compute_PieceIngredient()
    {
        var r = ReceiptLinePricing.Compute(30m, MeasureUnit.Piece, 8.40m, Ing(BaseUnit.Piece));

        Assert.Equal(ReceiptLineIssue.None, r.Issue);
        Assert.Equal(30m, r.BaseQuantity);
        Assert.Equal(0.28m, r.PricePerBaseUnit);
    }

    [Fact]
    public void Compute_PieceForGramIngredientWithWeight()
    {
        var r = ReceiptLinePricing.Compute(10m, MeasureUnit.Piece, 6.00m, Ing(BaseUnit.Gram, 60m));

        Assert.Equal(ReceiptLineIssue.None, r.Issue);
        Assert.Equal(600m, r.BaseQuantity);
        Assert.Equal(0.01m, r.PricePerBaseUnit);
    }

    [Fact]
    public void Compute_PieceForGramIngredientWithoutWeight_ReportsIncompatible()
    {
        var r = ReceiptLinePricing.Compute(10m, MeasureUnit.Piece, 6.00m, Ing(BaseUnit.Gram));

        Assert.Equal(ReceiptLineIssue.IncompatibleUnit, r.Issue);
        Assert.Null(r.PricePerBaseUnit);
        Assert.Null(r.BaseQuantity);
    }

    [Fact]
    public void Compute_LitreForGramIngredient_ReportsIncompatible()
    {
        var r = ReceiptLinePricing.Compute(2m, MeasureUnit.L, 6.00m, Ing(BaseUnit.Gram, 60m));

        Assert.Equal(ReceiptLineIssue.IncompatibleUnit, r.Issue);
        Assert.Null(r.PricePerBaseUnit);
    }

    [Fact]
    public void Compute_MissingIngredient()
    {
        var r = ReceiptLinePricing.Compute(2m, MeasureUnit.Kg, 6.00m, null);

        Assert.Equal(ReceiptLineIssue.MissingIngredient, r.Issue);
        Assert.Null(r.PricePerBaseUnit);
        Assert.Null(r.BaseQuantity);
    }

    [Fact]
    public void Compute_MissingIngredient_WinsOverMissingQuantity()
    {
        var r = ReceiptLinePricing.Compute(null, null, null, null);

        Assert.Equal(ReceiptLineIssue.MissingIngredient, r.Issue);
    }

    [Theory]
    [InlineData(null, MeasureUnit.Kg)]
    [InlineData(2.0, null)]
    [InlineData(null, null)]
    [InlineData(0.0, MeasureUnit.Kg)]
    [InlineData(-1.0, MeasureUnit.Kg)]
    public void Compute_MissingQuantity(double? quantity, MeasureUnit? unit)
    {
        var r = ReceiptLinePricing.Compute((decimal?)quantity, unit, 6.00m, Ing(BaseUnit.Gram));

        Assert.Equal(ReceiptLineIssue.MissingQuantity, r.Issue);
        Assert.Null(r.PricePerBaseUnit);
        Assert.Null(r.BaseQuantity);
    }

    [Fact]
    public void Compute_MissingQuantity_WinsOverInvalidTotal()
    {
        var r = ReceiptLinePricing.Compute(null, MeasureUnit.Kg, 0m, Ing(BaseUnit.Gram));

        Assert.Equal(ReceiptLineIssue.MissingQuantity, r.Issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Compute_ZeroTotal_Invalid(double? total)
    {
        var r = ReceiptLinePricing.Compute(2m, MeasureUnit.Kg, (decimal?)total, Ing(BaseUnit.Gram));

        Assert.Equal(ReceiptLineIssue.InvalidTotal, r.Issue);
        Assert.Null(r.PricePerBaseUnit);
        Assert.Null(r.BaseQuantity);
    }

    [Fact]
    public void Compute_InvalidTotal_WinsOverIncompatibleUnit()
    {
        var r = ReceiptLinePricing.Compute(10m, MeasureUnit.Piece, 0m, Ing(BaseUnit.Gram));

        Assert.Equal(ReceiptLineIssue.InvalidTotal, r.Issue);
    }

    [Fact]
    public void Compute_DoesNotRound()
    {
        var r = ReceiptLinePricing.Compute(3m, MeasureUnit.G, 1m, Ing(BaseUnit.Gram));

        Assert.Equal(1m / 3m, r.PricePerBaseUnit);
    }

    [Fact]
    public void ExclVat_TtcWithRate()
    {
        var excl = ReceiptLinePricing.ExclVat(10.60m, 0.06m, true, out var vatToCheck);

        Assert.Equal(10.00m, excl);
        Assert.False(vatToCheck);
    }

    [Fact]
    public void ExclVat_TtcWithoutRate_Flags()
    {
        var excl = ReceiptLinePricing.ExclVat(10.60m, null, true, out var vatToCheck);

        Assert.Equal(10.60m, excl);
        Assert.True(vatToCheck);
    }

    [Fact]
    public void ExclVat_TtcWithZeroRate_IsKnownRate()
    {
        var excl = ReceiptLinePricing.ExclVat(10.60m, 0m, true, out var vatToCheck);

        Assert.Equal(10.60m, excl);
        Assert.False(vatToCheck);
    }

    [Fact]
    public void ExclVat_TtcWithNegativeRate_TreatedAsUnknown()
    {
        var excl = ReceiptLinePricing.ExclVat(10.60m, -1m, true, out var vatToCheck);

        Assert.Equal(10.60m, excl);
        Assert.True(vatToCheck);
    }

    [Theory]
    [InlineData(0.06)]
    [InlineData(null)]
    public void ExclVat_Ht_Unchanged(double? rate)
    {
        var excl = ReceiptLinePricing.ExclVat(10.60m, (decimal?)rate, false, out var vatToCheck);

        Assert.Equal(10.60m, excl);
        Assert.False(vatToCheck);
    }

    [Fact]
    public void ExclVat_NoTotal_ReturnsNull()
    {
        var excl = ReceiptLinePricing.ExclVat(null, 0.06m, true, out var vatToCheck);

        Assert.Null(excl);
        Assert.False(vatToCheck);
    }
}
