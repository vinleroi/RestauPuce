namespace ChefOS.Tests;

public class SmokeTests
{
    [Fact]
    public void Solution_Builds_And_CoreAssembly_Loads()
        => Assert.Equal("ChefOS.Core", typeof(ChefOS.Core.Common.DomainValidationException).Assembly.GetName().Name);
}
