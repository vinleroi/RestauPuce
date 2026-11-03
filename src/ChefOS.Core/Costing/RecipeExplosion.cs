using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Core.Costing;

/// <summary>
/// Quantités brutes d'ingrédients, en unité de base, nécessaires pour des plats vendus ou une préparation produite :
/// la consommation théorique. Rendements et sous-préparations sont traités comme dans <see cref="RecipeBook"/> :
/// un plat qui utilise 100 ml d'un fond de 2000 ml consomme 5 % des ingrédients de ce fond.
/// Les lignes en défaut sont ignorées et signalées dans <see cref="ExplosionResult.Issues"/> ; rien ne lève d'exception.
/// Les éléments archivés restent comptés. L'instance est sans état entre deux appels.
/// </summary>
public sealed class RecipeExplosion
{
    private readonly Dictionary<int, Recipe> _recipesById;
    private readonly Dictionary<int, Ingredient> _ingredientsById;

    public RecipeExplosion(IEnumerable<Recipe> recipes, IEnumerable<Ingredient> ingredients)
    {
        _recipesById = recipes.ToDictionary(r => r.Id);
        _ingredientsById = ingredients.ToDictionary(i => i.Id);
    }

    /// <summary>
    /// Décompose <paramref name="quantity"/> d'une recette. Pour un plat, c'est un nombre de portions
    /// (<paramref name="unit"/> nul ou <see cref="MeasureUnit.Piece"/>). Pour une préparation, c'est une quantité dans
    /// <paramref name="unit"/> (l'unité de sortie de la préparation quand elle est nulle).
    /// </summary>
    public ExplosionResult Explode(int recipeId, decimal quantity, MeasureUnit? unit = null)
    {
        var totals = new Dictionary<int, decimal>();
        var issues = new List<CostIssue>();

        if (!_recipesById.TryGetValue(recipeId, out var recipe))
        {
            issues.Add(UnknownItem(recipeId, lineIndex: null));
            return Result(totals, issues);
        }

        decimal factor;
        if (recipe.Kind == RecipeKind.Dish)
        {
            if (unit is not (null or MeasureUnit.Piece))
            {
                issues.Add(new CostIssue(CostIssueKind.IncompatibleUnit, recipe.Name, LineIndex: null));
                return Result(totals, issues);
            }

            factor = quantity;
        }
        else
        {
            var scale = PreparationFactor(recipe, quantity, unit);
            if (scale.Failure is { } failure)
            {
                issues.Add(new CostIssue(failure, recipe.Name, LineIndex: null));
                return Result(totals, issues);
            }

            factor = scale.Factor;
        }

        Accumulate(recipe, factor, rootLineIndex: null, totals, issues, new HashSet<int>());
        return Result(totals, issues);
    }

    /// <summary>
    /// Parcourt les lignes d'une recette à l'échelle <paramref name="factor"/>. Les problèmes rencontrés dans une
    /// sous-préparation remontent sur la ligne de la recette décomposée (<paramref name="rootLineIndex"/>), sans doublon.
    /// </summary>
    private void Accumulate(Recipe recipe, decimal factor, int? rootLineIndex, Dictionary<int, decimal> totals, List<CostIssue> issues, HashSet<int> visiting)
    {
        visiting.Add(recipe.Id);
        try
        {
            for (var index = 0; index < recipe.Lines.Count; index++)
            {
                var line = recipe.Lines[index];
                var reportedIndex = rootLineIndex ?? index;

                if (line.IngredientId is { } ingredientId)
                    AccumulateIngredient(line, ingredientId, factor, reportedIndex, totals, issues);
                else if (line.SubRecipeId is { } subRecipeId)
                    AccumulateSubRecipe(line, subRecipeId, factor, reportedIndex, totals, issues, visiting);
                else
                    AddIssue(issues, UnknownItem(null, reportedIndex));
            }
        }
        finally
        {
            visiting.Remove(recipe.Id);
        }
    }

    private void AccumulateIngredient(RecipeLine line, int ingredientId, decimal factor, int reportedIndex, Dictionary<int, decimal> totals, List<CostIssue> issues)
    {
        if (!_ingredientsById.TryGetValue(ingredientId, out var ingredient))
        {
            AddIssue(issues, UnknownItem(ingredientId, reportedIndex));
            return;
        }

        var quantity = UnitConverter.ToBase(line.Quantity * factor, line.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
        if (!quantity.Success)
        {
            AddIssue(issues, new CostIssue(CostIssueKind.IncompatibleUnit, ingredient.Name, reportedIndex));
            return;
        }

        // Le rendement augmente la quantité brute à sortir du stock : 130 g utiles à 65 % de rendement = 200 g bruts.
        var gross = quantity.Value * 100m / ingredient.YieldPercent;
        totals[ingredientId] = totals.GetValueOrDefault(ingredientId) + gross;
    }

    private void AccumulateSubRecipe(RecipeLine line, int subRecipeId, decimal factor, int reportedIndex, Dictionary<int, decimal> totals, List<CostIssue> issues, HashSet<int> visiting)
    {
        if (!_recipesById.TryGetValue(subRecipeId, out var subRecipe))
        {
            AddIssue(issues, UnknownItem(subRecipeId, reportedIndex));
            return;
        }

        if (visiting.Contains(subRecipeId))
        {
            AddIssue(issues, new CostIssue(CostIssueKind.Cycle, subRecipe.Name, reportedIndex));
            return;
        }

        var scale = PreparationFactor(subRecipe, line.Quantity * factor, line.Unit);
        if (scale.Failure is { } failure)
        {
            AddIssue(issues, new CostIssue(failure, subRecipe.Name, reportedIndex));
            return;
        }

        Accumulate(subRecipe, scale.Factor, reportedIndex, totals, issues, visiting);
    }

    /// <summary>
    /// Part de la production d'une préparation que représente <paramref name="quantity"/> : la quantité, convertie dans l'unité
    /// de sortie, divisée par la quantité produite. Sans unité, la quantité est déjà dans l'unité de sortie.
    /// </summary>
    private static (decimal Factor, CostIssueKind? Failure) PreparationFactor(Recipe preparation, decimal quantity, MeasureUnit? unit)
    {
        if (preparation.OutputQuantity is not { } outputQuantity || outputQuantity <= 0m || preparation.OutputUnit is not { } outputUnit)
            return (0m, CostIssueKind.InvalidOutputQuantity);

        var converted = UnitConverter.ToBase(quantity, unit ?? NaturalUnit(outputUnit), outputUnit, averagePieceWeightGrams: null);
        return converted.Success
            ? (converted.Value / outputQuantity, null)
            : (0m, CostIssueKind.IncompatibleUnit);
    }

    private static MeasureUnit NaturalUnit(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => MeasureUnit.G,
        BaseUnit.Milliliter => MeasureUnit.Ml,
        _ => MeasureUnit.Piece
    };

    private static void AddIssue(List<CostIssue> issues, CostIssue issue)
    {
        if (!issues.Contains(issue))
            issues.Add(issue);
    }

    private static ExplosionResult Result(Dictionary<int, decimal> totals, List<CostIssue> issues)
        => new(totals, issues.AsReadOnly());

    /// <summary>L'identifiant tient lieu de nom : un élément inconnu n'en a pas.</summary>
    private static CostIssue UnknownItem(int? id, int? lineIndex)
        => new(CostIssueKind.UnknownItem, id is { } value ? $"#{value}" : string.Empty, lineIndex);
}

/// <summary>Quantités brutes par identifiant d'ingrédient, en unité de base de l'ingrédient, et problèmes rencontrés.</summary>
public sealed record ExplosionResult(IReadOnlyDictionary<int, decimal> GrossBaseQuantityByIngredient, IReadOnlyList<CostIssue> Issues);
