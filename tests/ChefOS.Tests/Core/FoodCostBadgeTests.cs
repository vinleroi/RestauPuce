using ChefOS.Core.Costing;

namespace ChefOS.Tests.Core;

public class FoodCostBadgeTests
{
    private const decimal Threshold = 0.35m;

    [Theory]
    [InlineData(0.25, FoodCostLevel.Good)]
    [InlineData(0.30, FoodCostLevel.Warning)]
    [InlineData(0.36, FoodCostLevel.Alert)]
    public void Classify_CompleteResult(double foodCost, FoodCostLevel expected)
        => Assert.Equal(expected, FoodCostBadge.Classify(Result(isComplete: true, (decimal)foodCost), Threshold));

    [Fact]
    public void Classify_IncompleteResult_IsIncomplete()
        => Assert.Equal(FoodCostLevel.Incomplete, FoodCostBadge.Classify(Result(isComplete: false, 0.25m), Threshold));

    [Fact]
    public void Classify_WithoutFoodCostPercent_IsIncomplete()
        => Assert.Equal(FoodCostLevel.Incomplete, FoodCostBadge.Classify(Result(isComplete: true, null), Threshold));

    [Fact]
    public void Classify_AlertThresholdBelowGoodLimit_StillAlerts()
        => Assert.Equal(FoodCostLevel.Alert, FoodCostBadge.Classify(Result(isComplete: true, 0.25m), alertThreshold: 0.20m));

    [Fact]
    public void Classify_Boundaries()
    {
        Assert.Equal(FoodCostLevel.Warning, FoodCostBadge.Classify(Result(true, FoodCostBadge.GoodBelow), Threshold));   // 0.28 : pas encore « bon »
        Assert.Equal(FoodCostLevel.Warning, FoodCostBadge.Classify(Result(true, Threshold), Threshold));                 // seuil exact : pas d'alerte
    }

    [Theory]
    [InlineData(0.25, FoodCostLevel.Good)]
    [InlineData(0.28, FoodCostLevel.Warning)]
    [InlineData(0.35, FoodCostLevel.Warning)]
    [InlineData(0.36, FoodCostLevel.Alert)]
    public void Classify_FromAFraction_FollowsTheSameBoundaries(double foodCost, FoodCostLevel expected)
        => Assert.Equal(expected, FoodCostBadge.Classify((decimal)foodCost, Threshold));

    private static CostResult Result(bool isComplete, decimal? foodCostPercent)
        => new(TotalCost: 4m, isComplete, Issues: [], Lines: [], PriceExclVat: 16m, foodCostPercent, Margin: 12m);
}
