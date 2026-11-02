namespace ChefOS.Core.Costing;

public enum FoodCostLevel
{
    Good,
    Warning,
    Alert,
    Incomplete
}

public static class FoodCostBadge
{
    /// <summary>Food cost strictement inférieur à cette fraction : pastille verte.</summary>
    public const decimal GoodBelow = 0.28m;

    /// <param name="alertThreshold">Fraction au-delà de laquelle le plat est en alerte (0.35m pour 35 %).</param>
    public static FoodCostLevel Classify(CostResult result, decimal alertThreshold)
    {
        if (!result.IsComplete || result.FoodCostPercent is not { } foodCost)
            return FoodCostLevel.Incomplete;

        if (foodCost > alertThreshold)
            return FoodCostLevel.Alert;

        return foodCost < GoodBelow ? FoodCostLevel.Good : FoodCostLevel.Warning;
    }
}
