using ChefOS.Core.Vitrine;

namespace ChefOS.Tests.Core.Vitrine;

public class CancellationPolicyTests
{
    [Fact]
    public void Cancellation_BoundaryInclusive()
    {
        var start = new DateTime(2026, 11, 10, 18, 0, 0, DateTimeKind.Utc);
        var boundary = start.AddHours(-24);

        Assert.True(CancellationPolicy.IsFreeCancellation(start, boundary, 24));
        Assert.True(CancellationPolicy.IsFreeCancellation(start, boundary.AddMinutes(-1), 24));
        Assert.False(CancellationPolicy.IsFreeCancellation(start, boundary.AddMinutes(1), 24));
    }
}
