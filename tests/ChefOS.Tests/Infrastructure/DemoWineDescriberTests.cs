using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Wines;

namespace ChefOS.Tests.Infrastructure;

public class DemoWineDescriberTests
{
    private static Task<WineDraft> Describe(string name, string? appellation = null, params string[] dishes) =>
        new DemoWineDescriber().DescribeAsync(new WineDescribeRequest(name, 2020, "Domaine", appellation, dishes));

    [Theory]
    [InlineData("Chardonnay du coin", WineColor.Blanc)]
    [InlineData("Côtes de Provence rosé", WineColor.Rose)]
    [InlineData("Crémant brut", WineColor.Effervescent)]
    [InlineData("Sauternes", WineColor.Doux)]
    [InlineData("Cuvée du patron", WineColor.Rouge)]
    public async Task Describe_GuessesTheColorFromTheName(string name, WineColor expected)
    {
        Assert.Equal(expected, (await Describe(name)).Color);
    }

    [Fact]
    public async Task Describe_IsGenericAndInventsNothing()
    {
        var draft = await Describe("Cuvée du patron", "Médoc");

        Assert.Equal("Médoc", draft.Appellation);
        Assert.Equal("", draft.Region);
        Assert.Equal("", draft.Country);
        Assert.Equal("", draft.Grapes);
        Assert.NotEmpty(draft.Robe);
        Assert.NotEmpty(draft.Nez);
        Assert.NotEmpty(draft.Bouche);
        Assert.NotEmpty(draft.Garde);
        Assert.NotEmpty(draft.ServiceTemperature);
        Assert.NotEmpty(draft.GeneralPairings);
    }

    [Fact]
    public async Task Describe_SuggestsAtMostTwoDishesOfTheList()
    {
        var draft = await Describe("Cuvée", null, "A", "B", "C");

        Assert.Equal(["A", "B"], draft.SuggestedPairingDishNames);
    }
}
