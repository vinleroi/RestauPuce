using ChefOS.Back.Components.Shared;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Units;

namespace ChefOS.Tests.Back;

public class UiTests
{
    [Fact]
    public void Fr_UsesCommaAsDecimalSeparator_SoFrenchEntryIsAccepted()
    {
        Assert.Equal(12.5m, decimal.Parse("12,50", Ui.Fr));
        Assert.Equal(87.5m, decimal.Parse("87,5", Ui.Fr));
    }

    [Theory]
    [InlineData(12.5, "12,50 €")]
    [InlineData(0.3, "0,30 €")]
    [InlineData(0, "0,00 €")]
    [InlineData(14.567, "14,57 €")]
    public void Money_FormatsWithTwoDecimalsAndEuroSign(double amount, string expected) =>
        Assert.Equal(expected, Ui.Money((decimal)amount));

    [Fact]
    public void Money_GroupsThousandsWithTheCultureSeparator() =>
        Assert.Equal($"1{Ui.Fr.NumberFormat.NumberGroupSeparator}234,50 €", Ui.Money(1234.5m));

    [Theory]
    [InlineData(0.875, "87,5 %")]
    [InlineData(1, "100 %")]
    [InlineData(0.12, "12 %")]
    [InlineData(0.2843, "28,43 %")]
    [InlineData(0, "0 %")]
    public void Percent_TakesAFractionAndTrimsUselessZeros(double fraction, string expected) =>
        Assert.Equal(expected, Ui.Percent((decimal)fraction));

    [Theory]
    [InlineData("0.0125", BaseUnit.Gram, "12,50 € / kg")]
    [InlineData("0.0036", BaseUnit.Milliliter, "3,60 € / l")]
    [InlineData("0.3", BaseUnit.Piece, "0,30 € / pièce")]
    public void PricePerDisplayUnit_ConvertsFromTheBaseUnit(string pricePerBase, BaseUnit unit, string expected) =>
        Assert.Equal(expected, Ui.PricePerDisplayUnit(decimal.Parse(pricePerBase, System.Globalization.CultureInfo.InvariantCulture), unit));

    [Fact]
    public void PricePerDisplayUnit_RoundsOnlyForDisplay() =>
        Assert.Equal("14,57 € / kg", Ui.PricePerDisplayUnit(0.0145678m, BaseUnit.Gram));

    [Fact]
    public void PricePerDisplayUnit_WithoutPrice_ShowsADash() =>
        Assert.Equal("—", Ui.PricePerDisplayUnit(null, BaseUnit.Gram));

    [Theory]
    [InlineData(BaseUnit.Gram, "Gramme")]
    [InlineData(BaseUnit.Milliliter, "Millilitre")]
    [InlineData(BaseUnit.Piece, "Pièce")]
    public void BaseUnitLabel_IsInFrench(BaseUnit unit, string expected) =>
        Assert.Equal(expected, Ui.BaseUnitLabel(unit));

    [Fact]
    public void Allergens_ListsTheFourteenEuAllergensInEnumOrder()
    {
        Assert.Equal(14, Ui.Allergens.Count);
        Assert.DoesNotContain(EuAllergen.None, Ui.Allergens);
        Assert.Equal(Ui.Allergens.OrderBy(a => (int)a), Ui.Allergens);
    }

    [Fact]
    public void AllergenLabel_GivesTheFrenchNameOfEachAllergen()
    {
        var labels = Ui.Allergens.Select(Ui.AllergenLabel).ToArray();

        Assert.Equal(
            ["Gluten", "Crustacés", "Œufs", "Poisson", "Arachides", "Soja", "Lait", "Fruits à coque", "Céleri", "Moutarde", "Sésame", "Sulfites", "Lupin", "Mollusques"],
            labels);
    }

    [Theory]
    [InlineData(EuAllergen.None)]
    [InlineData(EuAllergen.Milk | EuAllergen.Eggs)]
    public void AllergenLabel_RejectsNoneAndCombinations(EuAllergen value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Ui.AllergenLabel(value));

    [Theory]
    [InlineData("Échalotes", "echalo", true)]
    [InlineData("Échalotes", "ÉCHALOTES", true)]
    [InlineData("Crème fraîche liquide 35 %", "fraiche", true)]
    [InlineData("Carottes", "oignon", false)]
    [InlineData("Carottes", "", true)]
    [InlineData("Carottes", "   ", true)]
    [InlineData("Carottes", null, true)]
    [InlineData("Carottes", " carot ", true)]
    public void ContainsText_IgnoresCaseAndAccents(string text, string? term, bool expected) =>
        Assert.Equal(expected, Ui.ContainsText(text, term));

    [Theory]
    [InlineData(57.5, 64, 2)]    // étendue 6,5 : une graduation tous les 2 €
    [InlineData(12.5, 14, 1)]    // étendue 1,5 : le minimum accepté par MudChart
    [InlineData(0.26, 0.3, 1)]   // sous 1 € : le minimum accepté par MudChart
    [InlineData(10, 50, 10)]
    [InlineData(10, 90, 20)]
    [InlineData(14, 14, 5)]      // un seul prix distinct : 14 / 4 = 3,5, arrondi à 5
    [InlineData(40, 40, 10)]
    [InlineData(0, 0, 1)]
    public void AxisTickStep_AimsForAboutFourIntervals(double min, double max, int expected) =>
        Assert.Equal(expected, Ui.AxisTickStep(min, max));
}
