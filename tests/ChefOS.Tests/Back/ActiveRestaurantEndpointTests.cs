using ChefOS.Back.Tenancy;

namespace ChefOS.Tests.Back;

public class ActiveRestaurantEndpointTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/recipes")]
    [InlineData("/recipes/12?tab=lines#cout")]
    public void IsLocalUrl_AcceptsPathsOfThisApplication(string url) =>
        Assert.True(ActiveRestaurantEndpoint.IsLocalUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("recipes")]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\r\n/evil.example")]
    public void IsLocalUrl_RejectsAnythingThatCouldLeaveTheSite(string? url) =>
        Assert.False(ActiveRestaurantEndpoint.IsLocalUrl(url));
}
