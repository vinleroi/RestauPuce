namespace ChefOS.Core.Units;

public static class UnitConverter
{
    private const decimal Thousand = 1000m;

    private static readonly ConversionResult Failure = new(false, 0m);

    public static ConversionResult ToBase(decimal quantity, MeasureUnit unit, BaseUnit target, decimal? averagePieceWeightGrams)
    {
        var pieceWeight = averagePieceWeightGrams ?? 0m;

        return target switch
        {
            BaseUnit.Gram => unit switch
            {
                MeasureUnit.G => Ok(quantity),
                MeasureUnit.Kg => Ok(quantity * Thousand),
                MeasureUnit.Piece when pieceWeight > 0m => Ok(quantity * pieceWeight),
                _ => Failure
            },
            BaseUnit.Milliliter => unit switch
            {
                MeasureUnit.Ml => Ok(quantity),
                MeasureUnit.Cl => Ok(quantity * 10m),
                MeasureUnit.L => Ok(quantity * Thousand),
                _ => Failure
            },
            BaseUnit.Piece => unit switch
            {
                MeasureUnit.Piece => Ok(quantity),
                MeasureUnit.G when pieceWeight > 0m => Ok(quantity / pieceWeight),
                MeasureUnit.Kg when pieceWeight > 0m => Ok(quantity * Thousand / pieceWeight),
                _ => Failure
            },
            _ => Failure
        };
    }

    public static decimal PricePerBaseFromDisplay(decimal pricePerDisplayUnit, BaseUnit baseUnit)
        => baseUnit == BaseUnit.Piece ? pricePerDisplayUnit : pricePerDisplayUnit / Thousand;

    public static decimal PriceDisplayFromBase(decimal pricePerBaseUnit, BaseUnit baseUnit)
        => baseUnit == BaseUnit.Piece ? pricePerBaseUnit : pricePerBaseUnit * Thousand;

    public static string DisplayUnitLabel(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => "kg",
        BaseUnit.Milliliter => "l",
        BaseUnit.Piece => "pièce",
        _ => throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit, null)
    };

    private static ConversionResult Ok(decimal value) => new(true, value);
}
