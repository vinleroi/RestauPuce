using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Tests.Core;

public class RecipeBookTests
{
    private readonly RecipeBook book = BuildBook();

    // --- Coût d'une ligne ingrédient ---

    [Fact]
    public void IngredientLine_AppliesYield()
        => Assert.Equal(4.00m, Math.Round(book.Cost(Dish([IngLine(3, 130, MeasureUnit.G)])).TotalCost, 2));

    [Fact]
    public void MissingPrice_IsIncomplete_AndListsIngredient()
    {
        var r = book.Cost(Dish([IngLine(4, 1, MeasureUnit.G), IngLine(2, 100, MeasureUnit.G)]));
        Assert.False(r.IsComplete);
        Assert.Equal(1.00m, r.TotalCost);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.MissingPrice && i.ItemName == "Safran" && i.LineIndex == 0);
        Assert.Null(r.Lines[0].Cost);
        Assert.Equal(1.00m, r.Lines[1].Cost);
    }

    [Fact]
    public void IncompatibleUnit_IsReported()
    {
        var r = book.Cost(Dish([IngLine(6, 100, MeasureUnit.G)]));
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.IncompatibleUnit && i.ItemName == "Huile" && i.LineIndex == 0);
        Assert.Null(r.Lines[0].Cost);
        Assert.False(r.IsComplete);
    }

    [Fact]
    public void PieceIngredient_CountedPerPiece()
        => Assert.Equal(0.60m, book.Cost(Dish([IngLine(5, 2, MeasureUnit.Piece)])).TotalCost);

    [Fact]
    public void ArchivedIngredient_StillCosted()
        => Assert.Equal(4.20m, Math.Round(BuildBook(archiveSalmon: true).Cost(20).TotalCost, 2));

    // --- Sous-préparations ---

    [Fact]
    public void NestedPreparations_TwoLevels()
    {
        var r = book.Cost(20);
        Assert.True(r.IsComplete);
        Assert.Empty(r.Issues);
        Assert.Equal(4.20m, Math.Round(r.TotalCost, 2));             // 0,20 sauce + 4,00 saumon
        Assert.Equal(0.20m, Math.Round(r.Lines[0].Cost!.Value, 2));
        Assert.Equal(4.00m, Math.Round(r.Lines[1].Cost!.Value, 2));
    }

    [Fact]
    public void Preparation_WithZeroOutput_IsIncompleteNotCrash()   // Review Focus 3
    {
        var b = BuildBook(fondOutput: 0m);
        var r = b.Cost(20);
        Assert.False(r.IsComplete);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.InvalidOutputQuantity);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(null)]
    public void Preparation_WithNegativeOrMissingOutput_IsReportedOnTheParentLine(double? output)
    {
        var r = BuildBook(fondOutput: (decimal?)output).Cost(Prep(30, "Parent", [SubLine(10, 100, MeasureUnit.Ml)]));
        Assert.False(r.IsComplete);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.InvalidOutputQuantity && i.ItemName == "Fond de veau" && i.LineIndex == 0);
        Assert.Null(r.Lines[0].Cost);
    }

    [Fact]
    public void Preparation_WithoutOutputUnit_IsReportedAsInvalidOutput()
    {
        var noUnit = Prep(31, "Sans unité", [IngLine(2, 100, MeasureUnit.G)], outputQuantity: 500m, outputUnit: null);
        var b = new RecipeBook([noUnit], StandardIngredients());
        var r = b.Cost(Dish([SubLine(31, 100, MeasureUnit.Ml)]));
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.InvalidOutputQuantity && i.ItemName == "Sans unité");
    }

    [Fact]
    public void SubRecipeLine_WithIncompatibleUnit_IsReported()
    {
        var r = book.Cost(Dish([SubLine(11, 100, MeasureUnit.G)]));      // sauce en ml, quantité en g
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.IncompatibleUnit && i.ItemName == "Sauce beurre" && i.LineIndex == 0);
        Assert.Null(r.Lines[0].Cost);
    }

    [Fact]
    public void IncompletePreparation_PropagatesIssuesToParentLine_AndKeepsPartialCost()
    {
        var infusion = Prep(30, "Infusion", [IngLine(2, 100, MeasureUnit.G), IngLine(4, 1, MeasureUnit.G)],
            outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter);                 // 1,00 € chiffré + safran sans prix
        var b = new RecipeBook([infusion], StandardIngredients());

        var r = b.Cost(Dish([IngLine(2, 100, MeasureUnit.G), SubLine(30, 500, MeasureUnit.Ml)]));

        Assert.False(r.IsComplete);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.MissingPrice && i.ItemName == "Safran" && i.LineIndex == 1);
        Assert.Equal(0.50m, r.Lines[1].Cost);                                        // 500 ml × 1,00 € / 1000 ml (coût partiel)
        Assert.Equal(1.50m, r.TotalCost);
    }

    [Fact]
    public void PropagatedIssues_AreNotDuplicated()
    {
        var infusion = Prep(30, "Infusion", [IngLine(4, 1, MeasureUnit.G), IngLine(4, 2, MeasureUnit.G)],
            outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter);
        var b = new RecipeBook([infusion], StandardIngredients());

        var r = b.Cost(Dish([SubLine(30, 500, MeasureUnit.Ml)]));

        Assert.Single(r.Issues);
    }

    // --- Identifiants inconnus, cycles, mémoïsation ---

    [Fact]
    public void UnknownIds_AreReportedAsUnknownItem()
    {
        var r = book.Cost(Dish([IngLine(999, 1, MeasureUnit.G), SubLine(998, 1, MeasureUnit.Ml)]));
        Assert.False(r.IsComplete);
        Assert.Equal(0m, r.TotalCost);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.UnknownItem && i.LineIndex == 0);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.UnknownItem && i.LineIndex == 1);
        Assert.All(r.Lines, l => Assert.Null(l.Cost));
    }

    [Fact]
    public void Cost_OfUnknownRecipeId_ReportsUnknownItem()
    {
        var r = book.Cost(12345);
        Assert.False(r.IsComplete);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.UnknownItem);
    }

    [Fact]
    public void CyclicData_IsGuardedWhileCosting()
    {
        var a = Prep(1, "A", [SubLine(2, 100, MeasureUnit.Ml)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter);
        var b = Prep(2, "B", [SubLine(1, 100, MeasureUnit.Ml), IngLine(2, 100, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter);
        var cyclic = new RecipeBook([a, b], StandardIngredients());

        var r = cyclic.Cost(1);

        Assert.False(r.IsComplete);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.Cycle && i.LineIndex == 0);
        Assert.Contains(cyclic.Cost(2).Issues, i => i.Kind == CostIssueKind.Cycle);
    }

    [Fact]
    public void DraftReplacingAStoredRecipe_DetectsCycleEvenAfterCostWasCached()
    {
        _ = book.Cost(20);                                                           // remplit le cache avec les versions enregistrées
        var fondDraft = Prep(10, "Fond de veau", [SubLine(11, 100, MeasureUnit.Ml)], outputQuantity: 2000m, outputUnit: BaseUnit.Milliliter);

        var r = book.Cost(fondDraft);

        Assert.False(r.IsComplete);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.Cycle);
    }

    [Fact]
    public void Cost_ById_IsMemoized()
        => Assert.Same(book.Cost(20), book.Cost(20));

    [Fact]
    public void Cost_OfDraft_ResolvesLinesThroughTheBook()
    {
        var draft = Dish([SubLine(11, 10, MeasureUnit.Cl)]);                         // navigations nulles : tout passe par les ids
        Assert.Equal(0.20m, Math.Round(book.Cost(draft).TotalCost, 2));
    }

    // --- Prix, food cost, marge ---

    [Fact]
    public void Dish_FoodCostOnPriceExclVat()
    {
        var r = book.Cost(20);
        Assert.Equal(16.07m, Math.Round(r.PriceExclVat!.Value, 2));
        Assert.Equal(0.261m, Math.Round(r.FoodCostPercent!.Value, 3));
        Assert.Equal(11.87m, Math.Round(r.Margin!.Value, 2));
    }

    [Fact]
    public void Preparation_HasNoPriceFields()
    {
        var r = book.Cost(11);
        Assert.Null(r.PriceExclVat);
        Assert.Null(r.FoodCostPercent);
        Assert.Null(r.Margin);
    }

    [Fact]
    public void Dish_WithoutPriceOrVatRate_HasNoPriceFields()
    {
        var noPrice = Dish([IngLine(2, 100, MeasureUnit.G)]);
        noPrice.VatRate = 0.12m;
        var noVat = Dish([IngLine(2, 100, MeasureUnit.G)]);
        noVat.SellingPriceInclVat = 18m;

        foreach (var r in new[] { book.Cost(noPrice), book.Cost(noVat) })
        {
            Assert.Null(r.PriceExclVat);
            Assert.Null(r.FoodCostPercent);
            Assert.Null(r.Margin);
        }
    }

    [Fact]
    public void Dish_WithZeroPrice_HasNoFoodCostPercent()
    {
        var free = Dish([IngLine(2, 100, MeasureUnit.G)]);
        free.SellingPriceInclVat = 0m;
        free.VatRate = 0.12m;

        var r = book.Cost(free);

        Assert.Equal(0m, r.PriceExclVat);
        Assert.Null(r.FoodCostPercent);
        Assert.Equal(-1.00m, r.Margin);
    }

    [Theory]
    [InlineData(18.00, 0.12, 16.07)]
    [InlineData(10.60, 0.06, 10.00)]
    [InlineData(12.10, 0.21, 10.00)]
    [InlineData(9.50, 0.00, 9.50)]
    public void VatCalculator_ExclVat(double inclVat, double rate, double expected)
        => Assert.Equal((decimal)expected, Math.Round(VatCalculator.ExclVat((decimal)inclVat, (decimal)rate), 2));

    // --- Allergènes ---

    [Fact]
    public void Allergens_AggregateThroughPreparations()
        => Assert.Equal(EuAllergen.Milk | EuAllergen.Fish, book.Allergens(book.Recipes.Single(r => r.Id == 20)));

    [Fact]
    public void Allergens_OfCyclicDraft_Terminates()
    {
        var fondDraft = Prep(10, "Fond de veau", [SubLine(11, 100, MeasureUnit.Ml)], outputQuantity: 2000m, outputUnit: BaseUnit.Milliliter);
        Assert.Equal(EuAllergen.Milk, book.Allergens(fondDraft));
    }

    [Fact]
    public void Allergens_IgnoreUnknownItems_AndCountArchivedOnes()
    {
        var draft = Dish([IngLine(999, 1, MeasureUnit.G), IngLine(5, 1, MeasureUnit.Piece)]);
        Assert.Equal(EuAllergen.Eggs, BuildBook(archiveSalmon: true).Allergens(draft));
    }

    // --- Détection de cycle à l'édition ---

    [Fact]
    public void FindCycle_Indirect()
    {
        var fondDraft = Prep(10, "Fond de veau", [SubLine(11, 100, MeasureUnit.Ml)]);
        var c = book.FindCycle(fondDraft);
        Assert.Equal("Sauce beurre contient déjà Fond de veau (directement ou indirectement)", c!.Message);
    }

    [Fact]
    public void FindCycle_Self()
        => Assert.NotNull(book.FindCycle(Prep(10, "Fond de veau", [SubLine(10, 1, MeasureUnit.L)])));

    [Fact]
    public void FindCycle_NewRecipe_None()
        => Assert.Null(book.FindCycle(Dish([SubLine(11, 1, MeasureUnit.L)], id: 0)));

    [Fact]
    public void FindCycle_UnrelatedDraft_None()
        => Assert.Null(book.FindCycle(Prep(10, "Fond de veau", [IngLine(1, 1, MeasureUnit.Kg)])));

    // --- Données et helpers ---

    private static RecipeBook BuildBook(decimal? fondOutput = 2000m, bool archiveSalmon = false)
    {
        var ingredients = StandardIngredients();
        ingredients.Single(i => i.Id == 3).IsArchived = archiveSalmon;

        var recipes = new List<Recipe>
        {
            Prep(10, "Fond de veau", [IngLine(1, 1, MeasureUnit.Kg)], outputQuantity: fondOutput, outputUnit: BaseUnit.Milliliter),
            Prep(11, "Sauce beurre", [SubLine(10, 500, MeasureUnit.Ml), IngLine(2, 100, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter),
            Dish([SubLine(11, 10, MeasureUnit.Cl), IngLine(3, 130, MeasureUnit.G)], id: 20, name: "Saumon sauce", priceInclVat: 18.00m, vatRate: 0.12m)
        };
        return new RecipeBook(recipes, ingredients);
    }

    private static List<Ingredient> StandardIngredients() =>
    [
        Ing(1, "Os de veau", BaseUnit.Gram, 0.004m),
        Ing(2, "Beurre", BaseUnit.Gram, 0.010m, allergens: EuAllergen.Milk),
        Ing(3, "Saumon", BaseUnit.Gram, 0.020m, yield: 65m, allergens: EuAllergen.Fish),
        Ing(4, "Safran", BaseUnit.Gram, price: null),
        Ing(5, "Œuf", BaseUnit.Piece, 0.30m, allergens: EuAllergen.Eggs),
        Ing(6, "Huile", BaseUnit.Milliliter, 0.004m)
    ];

    private static Ingredient Ing(int id, string name, BaseUnit unit, decimal? price, decimal yield = 100m, EuAllergen allergens = EuAllergen.None)
        => new()
        {
            Id = id,
            Name = name,
            BaseUnit = unit,
            CurrentPricePerBaseUnit = price,
            YieldPercent = yield,
            Allergens = allergens
        };

    private static Recipe Dish(RecipeLine[] lines, int id = 0, string name = "Plat test", decimal? priceInclVat = null, decimal? vatRate = null)
        => new()
        {
            Id = id,
            Name = name,
            Kind = RecipeKind.Dish,
            SellingPriceInclVat = priceInclVat,
            VatRate = vatRate,
            Lines = [.. lines]
        };

    private static Recipe Prep(int id, string name, RecipeLine[] lines, decimal? outputQuantity = 1000m, BaseUnit? outputUnit = BaseUnit.Milliliter)
        => new()
        {
            Id = id,
            Name = name,
            Kind = RecipeKind.Preparation,
            OutputQuantity = outputQuantity,
            OutputUnit = outputUnit,
            Lines = [.. lines]
        };

    private static RecipeLine IngLine(int ingredientId, decimal quantity, MeasureUnit unit)
        => new() { IngredientId = ingredientId, Quantity = quantity, Unit = unit };

    private static RecipeLine SubLine(int subRecipeId, decimal quantity, MeasureUnit unit)
        => new() { SubRecipeId = subRecipeId, Quantity = quantity, Unit = unit };
}
