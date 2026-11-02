using ChefOS.Back.Components.Pages;
using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class OnboardingRulesTests
{
    private static MembershipSummary Owner(int id, string name) => new(id, name, MembershipRole.Owner);

    [Theory]
    [InlineData("Le Petit Bistro")]
    [InlineData("le petit bistro")]
    [InlineData("  LE PETIT BISTRO  ")]
    public void SecondSubmit_WithTheSameName_FindsTheRestaurantCreatedByTheFirst(string submitted)
    {
        var memberships = new[] { Owner(3, "Le Petit Bistro") };

        var found = OnboardingRules.FindOwnedRestaurant(memberships, submitted);

        Assert.Equal(3, found?.RestaurantId);
    }

    [Fact]
    public void AccentsAreSignificant_ButCaseOfAccentedLettersIsNot()
    {
        var memberships = new[] { Owner(5, "Café des Écoles") };

        Assert.Equal(5, OnboardingRules.FindOwnedRestaurant(memberships, "CAFÉ DES ÉCOLES")?.RestaurantId);
        Assert.Null(OnboardingRules.FindOwnedRestaurant(memberships, "Cafe des Ecoles"));
    }

    [Fact]
    public void DifferentName_FindsNothing_SoARealSecondRestaurantCanBeCreated()
    {
        var memberships = new[] { Owner(3, "Le Petit Bistro") };

        Assert.Null(OnboardingRules.FindOwnedRestaurant(memberships, "Le Grand Bistro"));
    }

    [Fact]
    public void NoMemberships_FindsNothing()
    {
        Assert.Null(OnboardingRules.FindOwnedRestaurant([], "Le Petit Bistro"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_FindsNothing_SoTheServiceCanRefuseIt(string? submitted)
    {
        var memberships = new[] { Owner(3, "Le Petit Bistro"), Owner(4, "") };

        Assert.Null(OnboardingRules.FindOwnedRestaurant(memberships, submitted));
    }

    [Theory]
    [InlineData(MembershipRole.Chef)]
    [InlineData(MembershipRole.Staff)]
    public void RestaurantOfSomeoneElse_IsNotReusedEvenWithTheSameName(MembershipRole role)
    {
        // Seul un restaurant créé par l'utilisateur (propriétaire) est repris ; un homonyme où il est invité ne l'empêche pas de créer le sien.
        var memberships = new[] { new MembershipSummary(9, "Le Petit Bistro", role) };

        Assert.Null(OnboardingRules.FindOwnedRestaurant(memberships, "Le Petit Bistro"));
    }
}
