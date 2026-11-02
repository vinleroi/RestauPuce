using ChefOS.Core.Units;

namespace ChefOS.Tests.Core;

public class UnitConverterTests
{
    [Theory]
    [InlineData(2, MeasureUnit.Kg, BaseUnit.Gram, null, 2000)]
    [InlineData(250, MeasureUnit.G, BaseUnit.Gram, null, 250)]
    [InlineData(25, MeasureUnit.Cl, BaseUnit.Milliliter, null, 250)]
    [InlineData(1.5, MeasureUnit.L, BaseUnit.Milliliter, null, 1500)]
    [InlineData(3, MeasureUnit.Piece, BaseUnit.Gram, 60.0, 180)]
    [InlineData(120, MeasureUnit.G, BaseUnit.Piece, 60.0, 2)]
    [InlineData(0.24, MeasureUnit.Kg, BaseUnit.Piece, 60.0, 4)]
    [InlineData(2, MeasureUnit.Piece, BaseUnit.Piece, null, 2)]
    public void ToBase_ValidConversions(double qty, MeasureUnit unit, BaseUnit target, double? pieceWeight, double expected)
    {
        var r = UnitConverter.ToBase((decimal)qty, unit, target, (decimal?)pieceWeight);
        Assert.True(r.Success);
        Assert.Equal((decimal)expected, r.Value);
    }

    [Theory]
    [InlineData(2, MeasureUnit.Piece, BaseUnit.Gram, null)]
    [InlineData(100, MeasureUnit.G, BaseUnit.Piece, null)]
    [InlineData(100, MeasureUnit.G, BaseUnit.Milliliter, null)]
    [InlineData(5, MeasureUnit.Ml, BaseUnit.Gram, null)]
    [InlineData(1, MeasureUnit.L, BaseUnit.Piece, 60.0)]
    public void ToBase_IncompatibleConversions_Fail(double qty, MeasureUnit unit, BaseUnit target, double? pieceWeight)
        => Assert.False(UnitConverter.ToBase((decimal)qty, unit, target, (decimal?)pieceWeight).Success);

    [Fact]
    public void Price_DisplayRoundTrip()
    {
        Assert.Equal(0.0125m, UnitConverter.PricePerBaseFromDisplay(12.50m, BaseUnit.Gram));
        Assert.Equal(12.50m, UnitConverter.PriceDisplayFromBase(0.0125m, BaseUnit.Gram));
        Assert.Equal(0.35m, UnitConverter.PricePerBaseFromDisplay(0.35m, BaseUnit.Piece));
        Assert.Equal("kg", UnitConverter.DisplayUnitLabel(BaseUnit.Gram));
    }
}
