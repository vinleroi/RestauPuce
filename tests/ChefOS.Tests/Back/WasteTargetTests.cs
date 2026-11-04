using ChefOS.Back.Components.Pages.Waste;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Tests.Back;

public class WasteTargetTests
{
    private static RecipeBook Book() => new(
        [
            new Recipe { Id = 10, Name = "Waterzooi", Kind = RecipeKind.Dish },
            new Recipe { Id = 11, Name = "Carbonade", Kind = RecipeKind.Dish },
            new Recipe { Id = 12, Name = "Ancien plat", Kind = RecipeKind.Dish, IsArchived = true },
            new Recipe { Id = 20, Name = "Fond de veau", Kind = RecipeKind.Preparation, OutputUnit = BaseUnit.Milliliter }
        ],
        [
            new Ingredient { Id = 1, Name = "Œufs", BaseUnit = BaseUnit.Piece },
            new Ingredient { Id = 2, Name = "Carottes", BaseUnit = BaseUnit.Gram, AveragePieceWeightGrams = 80m },
            new Ingredient { Id = 3, Name = "Vieux beurre", BaseUnit = BaseUnit.Gram, IsArchived = true }
        ]);

    [Fact]
    public void Candidates_ListIngredientsThenPreparationsThenDishes_EachByName_ArchivedLast()
    {
        var labels = WasteTarget.Candidates(Book()).Select(t => t.Label);

        Assert.Equal(
            [
                "🥕 Carottes", "🥕 Œufs", "🥕 Vieux beurre (archivé)",
                "🍲 Fond de veau",
                "🍽️ Carbonade", "🍽️ Waterzooi", "🍽️ Ancien plat (archivé)"
            ],
            labels);
    }

    [Fact]
    public void Candidates_CarryTheKindAndTheIdOfTheirSource()
    {
        var targets = WasteTarget.Candidates(Book());

        Assert.Equal((WasteTargetKind.Ingredient, 2), (targets[0].Kind, targets[0].Id));
        Assert.Equal((WasteTargetKind.Preparation, 20), (targets[3].Kind, targets[3].Id));
        Assert.Equal((WasteTargetKind.Dish, 11), (targets[4].Kind, targets[4].Id));
    }

    [Fact]
    public void ADish_IsLostInPortionsOnly()
    {
        var dish = WasteTarget.Candidates(Book()).Single(t => t.Name == "Waterzooi");

        Assert.Equal([MeasureUnit.Piece], dish.UnitChoices);
        Assert.Equal(MeasureUnit.Piece, dish.DefaultUnit);
        Assert.Equal("portions", dish.UnitLabel(MeasureUnit.Piece));
    }

    [Fact]
    public void APreparation_IsLostInTheUnitsOfItsOutputUnit()
    {
        var preparation = WasteTarget.Candidates(Book()).Single(t => t.Name == "Fond de veau");

        Assert.Equal([MeasureUnit.L, MeasureUnit.Cl, MeasureUnit.Ml], preparation.UnitChoices);
        Assert.Equal(MeasureUnit.L, preparation.DefaultUnit);
        Assert.Equal("l", preparation.UnitLabel(MeasureUnit.L));
    }

    [Fact]
    public void AnIngredient_IsLostInTheUnitsThatConvertToItsBaseUnit()
    {
        var targets = WasteTarget.Candidates(Book());

        Assert.Equal([MeasureUnit.Kg, MeasureUnit.G, MeasureUnit.Piece], targets.Single(t => t.Name == "Carottes").UnitChoices);
        Assert.Equal([MeasureUnit.Piece], targets.Single(t => t.Name == "Œufs").UnitChoices);
        Assert.Equal(MeasureUnit.Piece, targets.Single(t => t.Name == "Œufs").DefaultUnit);
    }

    [Fact]
    public void FindingTheText_IgnoresCaseAndAccents_AndTheEmojiPrefixOfTheFieldAfterSelection()
    {
        var targets = WasteTarget.Candidates(Book());

        Assert.Equal(["🥕 Œufs"], targets.Where(t => t.Matches("œufs")).Select(t => t.Label));
        Assert.Equal(["🥕 Carottes"], targets.Where(t => t.Matches("CAROTTE")).Select(t => t.Label));
        Assert.Equal(["🥕 Carottes"], targets.Where(t => t.Matches("🥕 Carottes")).Select(t => t.Label));
        Assert.Equal(targets.Count, targets.Count(t => t.Matches("")));
    }
}
