using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Tests.Core;

public class RecipeExplosionTests
{
    // Ingrédients : 1 Os de veau (g), 2 Beurre (g), 3 Saumon (g, rendement 65 %), 4 Œuf (pièce, 60 g), 5 Huile (ml).
    // Préparations : 10 Fond de veau (2000 ml ← 1 kg d'os), 11 Sauce beurre (1000 ml ← 500 ml de fond + 100 g de beurre).
    private const int Bones = 1;
    private const int Butter = 2;
    private const int Salmon = 3;
    private const int Egg = 4;
    private const int Oil = 5;
    private const int Stock = 10;
    private const int ButterSauce = 11;

    private readonly RecipeExplosion explosion = new(StandardRecipes(), StandardIngredients());

    // --- Lignes ingrédient ---

    [Fact]
    public void Explode_Dish_IngredientLinesWithYield()
    {
        // 2 portions × 130 g de saumon à 65 % de rendement = 400 g bruts.
        var r = Explode([Dish(20, [IngLine(Salmon, 130, MeasureUnit.G)])], 20, 2m);

        Assert.Empty(r.Issues);
        Assert.Equal(400m, Assert.Single(r.GrossBaseQuantityByIngredient).Value);
        Assert.Equal(400m, r.GrossBaseQuantityByIngredient[Salmon]);
    }

    [Fact]
    public void Explode_PieceIngredient()
    {
        var r = Explode([Dish(20, [IngLine(Egg, 2, MeasureUnit.Piece)])], 20, 1m);

        Assert.Empty(r.Issues);
        Assert.Equal(2m, r.GrossBaseQuantityByIngredient[Egg]);
    }

    [Fact]
    public void Explode_GramLineOnPieceIngredient_UsesAveragePieceWeight()
    {
        var r = Explode([Dish(20, [IngLine(Egg, 120, MeasureUnit.G)])], 20, 3m);

        Assert.Equal(6m, r.GrossBaseQuantityByIngredient[Egg]);              // 120 g / 60 g par pièce × 3 portions
    }

    [Fact]
    public void Explode_DishWithPieceUnit_IsTheSameAsPortions()
    {
        var r = Explode([Dish(20, [IngLine(Salmon, 130, MeasureUnit.G)])], 20, 2m, MeasureUnit.Piece);

        Assert.Empty(r.Issues);
        Assert.Equal(400m, r.GrossBaseQuantityByIngredient[Salmon]);
    }

    // --- Sous-préparations ---

    [Fact]
    public void Explode_ThroughPreparation_ProRata()
    {
        // Un plat utilise 100 ml de fond ; le fond fait 2000 ml pour 1 kg d'os : 100 / 2000 × 1000 g = 50 g d'os.
        var recipes = StandardRecipes();
        recipes.Add(Dish(21, [SubLine(Stock, 100, MeasureUnit.Ml)]));

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(21, 1m);

        Assert.Empty(r.Issues);
        Assert.Equal(50m, Assert.Single(r.GrossBaseQuantityByIngredient).Value);
        Assert.Equal(50m, r.GrossBaseQuantityByIngredient[Bones]);
    }

    [Fact]
    public void Explode_ThroughPreparation_ConvertsTheLineUnitIntoTheOutputUnit()
    {
        var recipes = StandardRecipes();
        recipes.Add(Dish(21, [SubLine(Stock, 10, MeasureUnit.Cl)]));          // 10 cl = 100 ml

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(21, 1m);

        Assert.Equal(50m, r.GrossBaseQuantityByIngredient[Bones]);
    }

    [Fact]
    public void Explode_TwoLevels_AccumulatesSameIngredient()
    {
        // 1 portion : 20 g de beurre en direct + 10 cl de sauce (100 ml × 100 g / 1000 ml = 10 g de beurre) = 30 g.
        // Fond dans la sauce : 100 ml de sauce contiennent 50 ml de fond = 25 g d'os.
        var recipes = StandardRecipes();
        recipes.Add(Dish(22, [SubLine(ButterSauce, 10, MeasureUnit.Cl), IngLine(Butter, 20, MeasureUnit.G), IngLine(Salmon, 130, MeasureUnit.G)]));

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(22, 1m);

        Assert.Empty(r.Issues);
        Assert.Equal(30m, r.GrossBaseQuantityByIngredient[Butter]);
        Assert.Equal(25m, r.GrossBaseQuantityByIngredient[Bones]);
        Assert.Equal(200m, r.GrossBaseQuantityByIngredient[Salmon]);
        Assert.Equal(3, r.GrossBaseQuantityByIngredient.Count);
    }

    [Fact]
    public void Explode_ScalesWithPortions()
    {
        var recipes = StandardRecipes();
        recipes.Add(Dish(22, [SubLine(ButterSauce, 10, MeasureUnit.Cl), IngLine(Butter, 20, MeasureUnit.G)]));

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(22, 4m);

        Assert.Equal(120m, r.GrossBaseQuantityByIngredient[Butter]);
        Assert.Equal(100m, r.GrossBaseQuantityByIngredient[Bones]);
    }

    [Fact]
    public void Explode_SameSubRecipeTwice_IsNotAFalseCycle()
    {
        var recipes = StandardRecipes();
        recipes.Add(Dish(23, [SubLine(Stock, 100, MeasureUnit.Ml), SubLine(Stock, 200, MeasureUnit.Ml)]));

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(23, 1m);

        Assert.Empty(r.Issues);
        Assert.Equal(150m, r.GrossBaseQuantityByIngredient[Bones]);
    }

    // --- Préparation décomposée directement ---

    [Fact]
    public void Explode_PreparationInItsOwnUnit()
    {
        // 1 L de fond = la moitié de la production (2000 ml) = 500 g d'os.
        var r = explosion.Explode(Stock, 1m, MeasureUnit.L);

        Assert.Empty(r.Issues);
        Assert.Equal(500m, Assert.Single(r.GrossBaseQuantityByIngredient).Value);
        Assert.Equal(500m, r.GrossBaseQuantityByIngredient[Bones]);
    }

    [Fact]
    public void Explode_Preparation_WithoutUnit_DefaultsToItsOutputUnit()
    {
        var r = explosion.Explode(Stock, 1000m);                              // 1000 ml

        Assert.Empty(r.Issues);
        Assert.Equal(500m, r.GrossBaseQuantityByIngredient[Bones]);
    }

    [Fact]
    public void Explode_PreparationWithGramOutput_DefaultsToGrams()
    {
        var recipes = new List<Recipe>
        {
            Prep(30, "Pâte", [IngLine(Butter, 250, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Gram)
        };

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(30, 400m);

        Assert.Equal(100m, r.GrossBaseQuantityByIngredient[Butter]);          // 400 g sur 1000 g de pâte × 250 g
    }

    [Fact]
    public void Explode_PreparationWithPieceOutput_DefaultsToPieces()
    {
        var recipes = new List<Recipe>
        {
            Prep(30, "Brioches", [IngLine(Butter, 200, MeasureUnit.G)], outputQuantity: 8m, outputUnit: BaseUnit.Piece)
        };

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(30, 2m);

        Assert.Equal(50m, r.GrossBaseQuantityByIngredient[Butter]);           // 2 brioches sur 8
    }

    // --- Problèmes : lignes ignorées, jamais d'exception ---

    [Fact]
    public void Explode_IncompatibleUnit_ReportedAndSkipped()
    {
        var recipes = new List<Recipe>
        {
            Dish(20, [IngLine(Oil, 100, MeasureUnit.G), IngLine(Butter, 100, MeasureUnit.G)])   // huile en ml, quantité en g
        };

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(20, 1m);

        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.IncompatibleUnit && i.ItemName == "Huile" && i.LineIndex == 0);
        Assert.DoesNotContain(Oil, r.GrossBaseQuantityByIngredient.Keys);
        Assert.Equal(100m, r.GrossBaseQuantityByIngredient[Butter]);
    }

    [Fact]
    public void Explode_SubRecipeLineWithIncompatibleUnit_ReportedAndSkipped()
    {
        var recipes = StandardRecipes();
        recipes.Add(Dish(21, [SubLine(Stock, 100, MeasureUnit.G), IngLine(Butter, 10, MeasureUnit.G)]));    // fond en ml, quantité en g

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(21, 1m);

        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.IncompatibleUnit && i.ItemName == "Fond de veau" && i.LineIndex == 0);
        Assert.DoesNotContain(Bones, r.GrossBaseQuantityByIngredient.Keys);
        Assert.Equal(10m, r.GrossBaseQuantityByIngredient[Butter]);
    }

    [Fact]
    public void Explode_DishWithNonPortionUnit_IsIncompatible()
    {
        var r = Explode([Dish(20, [IngLine(Salmon, 130, MeasureUnit.G)])], 20, 2m, MeasureUnit.Kg);

        Assert.Empty(r.GrossBaseQuantityByIngredient);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.IncompatibleUnit && i.ItemName == "Plat 20");
    }

    [Fact]
    public void Explode_PreparationInAnIncompatibleUnit_IsReported()
    {
        var r = explosion.Explode(Stock, 1m, MeasureUnit.Kg);                  // fond en ml, quantité en kg

        Assert.Empty(r.GrossBaseQuantityByIngredient);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.IncompatibleUnit && i.ItemName == "Fond de veau");
    }

    [Fact]
    public void Explode_ZeroOutput_ReportedAndSkipped()
    {
        var recipes = new List<Recipe>
        {
            Prep(Stock, "Fond de veau", [IngLine(Bones, 1, MeasureUnit.Kg)], outputQuantity: 0m, outputUnit: BaseUnit.Milliliter),
            Dish(21, [SubLine(Stock, 100, MeasureUnit.Ml), IngLine(Butter, 10, MeasureUnit.G)])
        };

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(21, 1m);

        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.InvalidOutputQuantity && i.ItemName == "Fond de veau" && i.LineIndex == 0);
        Assert.DoesNotContain(Bones, r.GrossBaseQuantityByIngredient.Keys);
        Assert.Equal(10m, r.GrossBaseQuantityByIngredient[Butter]);
    }

    [Fact]
    public void Explode_PreparationWithoutOutputUnit_IsReportedWhenExplodedDirectly()
    {
        var recipes = new List<Recipe>
        {
            Prep(Stock, "Fond de veau", [IngLine(Bones, 1, MeasureUnit.Kg)], outputQuantity: 2000m, outputUnit: null)
        };

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(Stock, 1m, MeasureUnit.L);

        Assert.Empty(r.GrossBaseQuantityByIngredient);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.InvalidOutputQuantity && i.ItemName == "Fond de veau");
    }

    [Fact]
    public void Explode_UnknownItems_AreReported()
    {
        var recipes = new List<Recipe>
        {
            Dish(20, [IngLine(999, 1, MeasureUnit.G), SubLine(998, 1, MeasureUnit.Ml), IngLine(Butter, 10, MeasureUnit.G)])
        };

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(20, 1m);

        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.UnknownItem && i.LineIndex == 0);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.UnknownItem && i.LineIndex == 1);
        Assert.Equal(10m, Assert.Single(r.GrossBaseQuantityByIngredient).Value);
    }

    [Fact]
    public void Explode_UnknownRecipe_IsReported()
    {
        var r = explosion.Explode(12345, 1m);

        Assert.Empty(r.GrossBaseQuantityByIngredient);
        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.UnknownItem);
    }

    [Fact]
    public void Explode_Cycle_DoesNotLoop()
    {
        var recipes = new List<Recipe>
        {
            Prep(1, "A", [SubLine(2, 100, MeasureUnit.Ml), IngLine(Butter, 50, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter),
            Prep(2, "B", [SubLine(1, 100, MeasureUnit.Ml), IngLine(Bones, 100, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter),
            Dish(3, [SubLine(1, 500, MeasureUnit.Ml)]),
            Prep(4, "Auto-référence", [SubLine(4, 100, MeasureUnit.Ml), IngLine(Butter, 10, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter)
        };
        var cyclic = new RecipeExplosion(recipes, StandardIngredients());

        var r = cyclic.Explode(3, 1m);

        Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.Cycle && i.ItemName == "A");
        Assert.Equal(25m, r.GrossBaseQuantityByIngredient[Butter]);            // la ligne cyclique est ignorée, le reste est compté
        Assert.Equal(5m, r.GrossBaseQuantityByIngredient[Bones]);              // 500 ml de A → 50 ml de B → 5 g d'os

        var self = cyclic.Explode(4, 1m, MeasureUnit.L);
        Assert.Contains(self.Issues, i => i.Kind == CostIssueKind.Cycle && i.ItemName == "Auto-référence");
        Assert.Equal(10m, self.GrossBaseQuantityByIngredient[Butter]);
    }

    [Fact]
    public void Explode_IssuesAreNotDuplicated()
    {
        var recipes = StandardRecipes();
        recipes.Add(Prep(30, "Infusion", [IngLine(Oil, 1, MeasureUnit.G), IngLine(Oil, 2, MeasureUnit.G)]));  // deux lignes fautives
        recipes.Add(Dish(21, [SubLine(30, 500, MeasureUnit.Ml)]));

        var r = new RecipeExplosion(recipes, StandardIngredients()).Explode(21, 1m);

        // Les problèmes remontent sur la ligne du plat (index 0) et ne sont listés qu'une fois.
        Assert.Single(r.Issues);
        Assert.Equal(0, r.Issues[0].LineIndex);
    }

    // --- Cohérence avec le coût de revient ---

    [Fact]
    public void Explode_AgreesWithCosting()
    {
        // Le coût d'un plat est la somme (quantité brute × prix) : la consommation théorique doit donner le même total.
        var recipes = StandardRecipes();
        recipes.Add(Dish(22, [SubLine(ButterSauce, 10, MeasureUnit.Cl), IngLine(Butter, 20, MeasureUnit.G), IngLine(Salmon, 130, MeasureUnit.G), IngLine(Egg, 2, MeasureUnit.Piece)]));
        var ingredients = StandardIngredients();

        var cost = new RecipeBook(recipes, ingredients).Cost(22).TotalCost;
        var consumption = new RecipeExplosion(recipes, ingredients).Explode(22, 1m).GrossBaseQuantityByIngredient
            .Sum(kv => kv.Value * ingredients.Single(i => i.Id == kv.Key).CurrentPricePerBaseUnit!.Value);

        Assert.Equal(Math.Round(cost, 6), Math.Round(consumption, 6));
    }

    // --- Données et helpers ---

    private static ExplosionResult Explode(List<Recipe> recipes, int recipeId, decimal quantity, MeasureUnit? unit = null)
        => new RecipeExplosion(recipes, StandardIngredients()).Explode(recipeId, quantity, unit);

    private static List<Recipe> StandardRecipes() =>
    [
        Prep(Stock, "Fond de veau", [IngLine(Bones, 1, MeasureUnit.Kg)], outputQuantity: 2000m, outputUnit: BaseUnit.Milliliter),
        Prep(ButterSauce, "Sauce beurre", [SubLine(Stock, 500, MeasureUnit.Ml), IngLine(Butter, 100, MeasureUnit.G)], outputQuantity: 1000m, outputUnit: BaseUnit.Milliliter)
    ];

    private static List<Ingredient> StandardIngredients() =>
    [
        Ing(Bones, "Os de veau", BaseUnit.Gram, 0.004m),
        Ing(Butter, "Beurre", BaseUnit.Gram, 0.010m),
        Ing(Salmon, "Saumon", BaseUnit.Gram, 0.020m, yield: 65m),
        Ing(Egg, "Œuf", BaseUnit.Piece, 0.30m, pieceWeightGrams: 60m),
        Ing(Oil, "Huile", BaseUnit.Milliliter, 0.004m)
    ];

    private static Ingredient Ing(int id, string name, BaseUnit unit, decimal price, decimal yield = 100m, decimal? pieceWeightGrams = null)
        => new()
        {
            Id = id,
            Name = name,
            BaseUnit = unit,
            CurrentPricePerBaseUnit = price,
            YieldPercent = yield,
            AveragePieceWeightGrams = pieceWeightGrams
        };

    private static Recipe Dish(int id, RecipeLine[] lines)
        => new() { Id = id, Name = $"Plat {id}", Kind = RecipeKind.Dish, Lines = [.. lines] };

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
