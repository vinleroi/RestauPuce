using ChefOS.Back.Components.Pages.Ingredients;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class IngredientFormTests
{
    [Theory]
    [InlineData(BaseUnit.Gram, 12.5, 0.0125)]
    [InlineData(BaseUnit.Milliliter, 4.2, 0.0042)]
    [InlineData(BaseUnit.Piece, 0.3, 0.3)]
    public void ToInput_ConvertsTheDisplayPriceToThePricePerBaseUnit(BaseUnit unit, double displayPrice, double expectedPerBase)
    {
        var form = new IngredientForm { Name = "X", BaseUnit = unit, DisplayPrice = (decimal)displayPrice };

        Assert.Equal((decimal)expectedPerBase, form.ToInput(null).PricePerBaseUnit);
    }

    [Fact]
    public void ToInput_DoesNotRoundThePriceBeforeConversion()
    {
        var form = new IngredientForm { Name = "X", BaseUnit = BaseUnit.Gram, DisplayPrice = 12.3456789m };

        Assert.Equal(0.0123456789m, form.ToInput(null).PricePerBaseUnit);
    }

    [Fact]
    public void ToInput_WithoutPrice_LeavesThePriceUntouched()
    {
        var form = new IngredientForm { Name = "X", DisplayPrice = null };

        Assert.Null(form.ToInput(7).PricePerBaseUnit);
    }

    [Fact]
    public void ToInput_CarriesTheOtherFieldsAndTheId()
    {
        var form = new IngredientForm
        {
            Name = "Paleron",
            Category = "Viandes",
            BaseUnit = BaseUnit.Gram,
            YieldPercent = 87.5m,
            AveragePieceWeightGrams = 120m,
            SupplierId = 3,
            Allergens = EuAllergen.Celery
        };

        var input = form.ToInput(42);

        Assert.Equal(new IngredientInput(42, "Paleron", "Viandes", EuAllergen.Celery, BaseUnit.Gram, null, 87.5m, 120m, 3), input);
    }

    [Fact]
    public void ToInput_ClearedYield_BecomesZero_SoTheServiceRejectsIt()
    {
        var form = new IngredientForm { Name = "X", YieldPercent = null };

        Assert.Equal(0m, form.ToInput(null).YieldPercent);
    }

    [Fact]
    public void ToInput_WithoutCategory_SendsAnEmptyOne_SoTheServiceAppliesItsDefault()
    {
        var form = new IngredientForm { Name = "X", Category = null };

        Assert.Equal("", form.ToInput(null).Category);
    }

    [Fact]
    public void From_ExpressesThePriceInTheDisplayUnit()
    {
        var ingredient = new Ingredient
        {
            Name = "Os de veau",
            Category = "Viandes",
            BaseUnit = BaseUnit.Gram,
            CurrentPricePerBaseUnit = 0.0032m,
            YieldPercent = 100m,
            SupplierId = 1
        };

        var form = IngredientForm.From(ingredient);

        Assert.Equal(3.2m, form.DisplayPrice);
        Assert.Equal("Viandes", form.Category);
        Assert.Equal(100m, form.YieldPercent);
        Assert.Equal(1, form.SupplierId);
    }

    [Fact]
    public void From_WithoutPrice_HasNoDisplayPrice()
    {
        var form = IngredientForm.From(new Ingredient { Name = "Sel", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = null });

        Assert.Null(form.DisplayPrice);
    }

    [Theory]
    [InlineData(BaseUnit.Gram, "0.014567")]
    [InlineData(BaseUnit.Milliliter, "0.003200")]
    [InlineData(BaseUnit.Piece, "0.300000")]
    public void FromThenToInput_RestoresTheStoredPriceExactly(BaseUnit unit, string stored)
    {
        var price = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);
        var ingredient = new Ingredient { Name = "X", BaseUnit = unit, CurrentPricePerBaseUnit = price };

        var input = IngredientForm.From(ingredient).ToInput(1);

        Assert.Equal(price, input.PricePerBaseUnit);
    }

    [Fact]
    public void ChangeUnit_ClearsThePriceTypedForTheOldUnit()
    {
        var form = new IngredientForm { Name = "Oeuf", BaseUnit = BaseUnit.Piece, DisplayPrice = 0.30m };

        form.ChangeUnit(BaseUnit.Gram);

        Assert.Equal(BaseUnit.Gram, form.BaseUnit);
        Assert.Null(form.DisplayPrice);
    }

    [Fact]
    public void ChangeUnit_ToTheSameUnit_KeepsThePrice()
    {
        var form = new IngredientForm { Name = "Oeuf", BaseUnit = BaseUnit.Piece, DisplayPrice = 0.30m };

        form.ChangeUnit(BaseUnit.Piece);

        Assert.Equal(BaseUnit.Piece, form.BaseUnit);
        Assert.Equal(0.30m, form.DisplayPrice);
    }

    [Fact]
    public void ChangeUnit_ThenToInput_SendsNoPrice_UntilItIsTypedAgainInTheNewUnit()
    {
        var form = IngredientForm.From(new Ingredient
        {
            Name = "Oeuf", BaseUnit = BaseUnit.Piece, CurrentPricePerBaseUnit = 0.30m, YieldPercent = 100m
        });

        form.ChangeUnit(BaseUnit.Gram);
        var untouched = form.ToInput(4);

        // Sans ressaisie : unité de base changée, prix absent (le service refuse alors l'enregistrement).
        Assert.Equal(BaseUnit.Gram, untouched.BaseUnit);
        Assert.Null(untouched.PricePerBaseUnit);

        form.DisplayPrice = 3m; // 3 €/kg
        Assert.Equal(0.003m, form.ToInput(4).PricePerBaseUnit);
    }

    [Fact]
    public void SetAllergen_AddsAndRemovesFlagsIndependently()
    {
        var form = new IngredientForm();

        form.SetAllergen(EuAllergen.Milk, true);
        form.SetAllergen(EuAllergen.Eggs, true);
        form.SetAllergen(EuAllergen.Milk, false);

        Assert.Equal(EuAllergen.Eggs, form.Allergens);
        Assert.True(form.HasAllergen(EuAllergen.Eggs));
        Assert.False(form.HasAllergen(EuAllergen.Milk));
    }
}
