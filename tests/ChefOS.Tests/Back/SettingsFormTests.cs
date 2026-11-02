using ChefOS.Back.Components.Pages;
using ChefOS.Core.Common;
using ChefOS.Core.Restaurants;

namespace ChefOS.Tests.Back;

public class SettingsFormTests
{
    private static SettingsForm Valid() => new()
    {
        Name = "Le Petit Bistro",
        Slug = "le-petit-bistro",
        FoodVatPercent = 12m,
        DrinkVatPercent = 21m,
        AlertThresholdPercent = 35m
    };

    [Fact]
    public void ToInput_ConvertsThePercentagesToFractions()
    {
        var input = Valid().ToInput();

        Assert.Equal(0.12m, input.DefaultFoodVatRate);
        Assert.Equal(0.21m, input.DefaultDrinkVatRate);
        Assert.Equal(0.35m, input.FoodCostAlertThreshold);
    }

    [Fact]
    public void ToInput_KeepsDecimalPercentagesExact()
    {
        var form = Valid();
        form.FoodVatPercent = 5.5m;
        form.AlertThresholdPercent = 32.5m;

        var input = form.ToInput();

        Assert.Equal(0.055m, input.DefaultFoodVatRate);
        Assert.Equal(0.325m, input.FoodCostAlertThreshold);
    }

    [Fact]
    public void From_ShowsTheStoredFractionsAsPercentages()
    {
        var restaurant = new Restaurant
        {
            Name = "Brasserie",
            Slug = "brasserie",
            Address = "Rue du Marché 1",
            VatNumber = "BE0123.456.789",
            DefaultFoodVatRate = 0.06m,
            DefaultDrinkVatRate = 0.21m,
            FoodCostAlertThreshold = 0.30m
        };

        var form = SettingsForm.From(restaurant);

        Assert.Equal("Brasserie", form.Name);
        Assert.Equal("brasserie", form.Slug);
        Assert.Equal("Rue du Marché 1", form.Address);
        Assert.Equal("BE0123.456.789", form.VatNumber);
        Assert.Equal(6m, form.FoodVatPercent);
        Assert.Equal(21m, form.DrinkVatPercent);
        Assert.Equal(30m, form.AlertThresholdPercent);
    }

    [Fact]
    public void FromThenToInput_RoundTripsTheStoredValues()
    {
        var restaurant = new Restaurant
        {
            Name = "Brasserie",
            Slug = "brasserie",
            DefaultFoodVatRate = 0.055m,
            DefaultDrinkVatRate = 0.21m,
            FoodCostAlertThreshold = 0.325m
        };

        var input = SettingsForm.From(restaurant).ToInput();

        Assert.Equal(0.055m, input.DefaultFoodVatRate);
        Assert.Equal(0.21m, input.DefaultDrinkVatRate);
        Assert.Equal(0.325m, input.FoodCostAlertThreshold);
    }

    [Fact]
    public void ToInput_TrimsTheSlug()
    {
        var form = Valid();
        form.Slug = "  le-petit-bistro ";

        Assert.Equal("le-petit-bistro", form.ToInput().Slug);
    }

    [Theory]
    [InlineData("food", "Le taux de TVA nourriture est obligatoire")]
    [InlineData("drink", "Le taux de TVA boissons est obligatoire")]
    [InlineData("threshold", "Le seuil d'alerte est obligatoire")]
    public void ToInput_WithAClearedPercentage_IsRefusedInsteadOfSavingZero(string cleared, string expectedMessage)
    {
        var form = Valid();
        switch (cleared)
        {
            case "food": form.FoodVatPercent = null; break;
            case "drink": form.DrinkVatPercent = null; break;
            default: form.AlertThresholdPercent = null; break;
        }

        var ex = Assert.Throws<DomainValidationException>(() => form.ToInput());

        Assert.Equal(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("le-petit-bistro", "/le-petit-bistro")]
    [InlineData("  bistro ", "/bistro")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void PublicPath_PreviewsThePublicLink(string slug, string? expected)
    {
        var form = Valid();
        form.Slug = slug;

        Assert.Equal(expected, form.PublicPath);
    }
}
