using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Core.Costing;

/// <summary>
/// Calcule le coût de revient, les allergènes et les cycles d'un ensemble de recettes en mémoire.
/// Les lignes sont résolues par <c>IngredientId</c> / <c>SubRecipeId</c> dans les dictionnaires construits ici,
/// jamais via les propriétés de navigation : un brouillon non enregistré passé à <see cref="Cost(Recipe)"/> n'en a pas.
/// Les éléments archivés restent chiffrés. L'instance n'est pas thread-safe (cache de coûts).
/// </summary>
public sealed class RecipeBook
{
    private readonly Dictionary<int, Recipe> _recipesById;
    private readonly Dictionary<int, Ingredient> _ingredientsById;
    private readonly Dictionary<int, CostResult> _memo = new();

    public RecipeBook(IEnumerable<Recipe> recipes, IEnumerable<Ingredient> ingredients)
    {
        Recipes = recipes.ToList();
        Ingredients = ingredients.ToList();
        _recipesById = Recipes.ToDictionary(r => r.Id);
        _ingredientsById = Ingredients.ToDictionary(i => i.Id);
    }

    public IReadOnlyList<Recipe> Recipes { get; }

    public IReadOnlyList<Ingredient> Ingredients { get; }

    /// <summary>Coût d'une recette enregistrée (mémoïsé).</summary>
    public CostResult Cost(int recipeId)
    {
        if (_memo.TryGetValue(recipeId, out var cached))
            return cached;

        if (!_recipesById.TryGetValue(recipeId, out var recipe))
            return new CostResult(0m, false, [UnknownItem(recipeId, lineIndex: null)], [], null, null, null);

        // Un calcul racine ne dépend d'aucun contexte : toujours mémoïsable.
        var result = Compute(recipe, _memo, new HashSet<int>());
        _memo[recipeId] = result;
        return result;
    }

    /// <summary>
    /// Coût d'une recette quelconque, y compris un brouillon non enregistré ou une version modifiée d'une recette existante.
    /// Non mémoïsé et sans le cache de <see cref="Cost(int)"/> : les sous-recettes enregistrées sont rechiffrées
    /// pour que le brouillon (identifié par son <c>Id</c>) soit bien vu dans les cycles.
    /// </summary>
    public CostResult Cost(Recipe recipe) => Compute(recipe, new Dictionary<int, CostResult>(), new HashSet<int>());

    /// <summary>OU des allergènes de tous les ingrédients de la recette, sous-préparations comprises.</summary>
    public EuAllergen Allergens(Recipe recipe)
    {
        var allergens = EuAllergen.None;
        CollectAllergens(recipe, new HashSet<int> { recipe.Id }, ref allergens);
        return allergens;
    }

    /// <summary>
    /// Cycle créé par ce brouillon : une de ses lignes référence une sous-recette qui est le brouillon lui-même
    /// ou qui le contient (directement ou non). Un brouillon neuf (<c>Id == 0</c>) ne peut pas encore être contenu.
    /// </summary>
    public CycleInfo? FindCycle(Recipe draft)
    {
        foreach (var line in draft.Lines)
        {
            if (line.SubRecipeId is not { } subRecipeId)
                continue;

            if (subRecipeId == draft.Id)
                return new CycleInfo(draft.Name, draft.Name);

            if (draft.Id != 0
                && _recipesById.TryGetValue(subRecipeId, out var subRecipe)
                && Contains(subRecipe, draft.Id, new HashSet<int>()))
                return new CycleInfo(subRecipe.Name, draft.Name);
        }

        return null;
    }

    private CostResult Compute(Recipe recipe, Dictionary<int, CostResult> memo, HashSet<int> visiting)
    {
        visiting.Add(recipe.Id);
        try
        {
            var issues = new List<CostIssue>();
            var lines = new List<LineCost>(recipe.Lines.Count);
            for (var index = 0; index < recipe.Lines.Count; index++)
                lines.Add(new LineCost(index, CostLine(recipe.Lines[index], index, issues, memo, visiting)));

            var totalCost = lines.Sum(l => l.Cost ?? 0m);

            decimal? priceExclVat = null;
            decimal? foodCostPercent = null;
            decimal? margin = null;
            if (recipe.Kind == RecipeKind.Dish && recipe.SellingPriceInclVat is { } inclVat && recipe.VatRate is { } vatRate)
            {
                priceExclVat = VatCalculator.ExclVat(inclVat, vatRate);
                foodCostPercent = priceExclVat == 0m ? null : totalCost / priceExclVat;
                margin = priceExclVat - totalCost;
            }

            return new CostResult(totalCost, issues.Count == 0, issues.AsReadOnly(), lines.AsReadOnly(),
                priceExclVat, foodCostPercent, margin);
        }
        finally
        {
            visiting.Remove(recipe.Id);
        }
    }

    private decimal? CostLine(RecipeLine line, int index, List<CostIssue> issues, Dictionary<int, CostResult> memo, HashSet<int> visiting)
    {
        if (line.IngredientId is { } ingredientId)
            return CostIngredientLine(line, ingredientId, index, issues);

        if (line.SubRecipeId is { } subRecipeId)
            return CostSubRecipeLine(line, subRecipeId, index, issues, memo, visiting);

        issues.Add(UnknownItem(null, index));
        return null;
    }

    private decimal? CostIngredientLine(RecipeLine line, int ingredientId, int index, List<CostIssue> issues)
    {
        if (!_ingredientsById.TryGetValue(ingredientId, out var ingredient))
        {
            issues.Add(UnknownItem(ingredientId, index));
            return null;
        }

        if (ingredient.CurrentPricePerBaseUnit is not { } pricePerBaseUnit)
        {
            issues.Add(new CostIssue(CostIssueKind.MissingPrice, ingredient.Name, index));
            return null;
        }

        var quantity = UnitConverter.ToBase(line.Quantity, line.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
        if (!quantity.Success)
        {
            issues.Add(new CostIssue(CostIssueKind.IncompatibleUnit, ingredient.Name, index));
            return null;
        }

        // Le rendement augmente la quantité brute à acheter : 130 g utiles à 65 % de rendement = 200 g payés.
        return quantity.Value * 100m / ingredient.YieldPercent * pricePerBaseUnit;
    }

    private decimal? CostSubRecipeLine(RecipeLine line, int subRecipeId, int index, List<CostIssue> issues, Dictionary<int, CostResult> memo, HashSet<int> visiting)
    {
        if (!_recipesById.TryGetValue(subRecipeId, out var subRecipe))
        {
            issues.Add(UnknownItem(subRecipeId, index));
            return null;
        }

        if (visiting.Contains(subRecipeId))
        {
            issues.Add(new CostIssue(CostIssueKind.Cycle, subRecipe.Name, index));
            return null;
        }

        var subCost = CostNested(subRecipe, memo, visiting);

        // Les problèmes de la préparation remontent sur la ligne parente ; la ligne reste chiffrée sur le coût partiel.
        foreach (var issue in subCost.Issues)
        {
            var lifted = issue with { LineIndex = index };
            if (!issues.Contains(lifted))
                issues.Add(lifted);
        }

        if (subRecipe.OutputQuantity is not { } outputQuantity || outputQuantity <= 0m || subRecipe.OutputUnit is not { } outputUnit)
        {
            issues.Add(new CostIssue(CostIssueKind.InvalidOutputQuantity, subRecipe.Name, index));
            return null;
        }

        var quantity = UnitConverter.ToBase(line.Quantity, line.Unit, outputUnit, averagePieceWeightGrams: null);
        if (!quantity.Success)
        {
            issues.Add(new CostIssue(CostIssueKind.IncompatibleUnit, subRecipe.Name, index));
            return null;
        }

        return quantity.Value * subCost.TotalCost / outputQuantity;
    }

    /// <summary>
    /// Coût d'une sous-recette en cours de calcul d'une recette parente. Un résultat qui contient un cycle dépend de la pile
    /// de visite : on ne le met pas en cache. Sans cycle, il est identique quel que soit le contexte.
    /// </summary>
    private CostResult CostNested(Recipe subRecipe, Dictionary<int, CostResult> memo, HashSet<int> visiting)
    {
        if (memo.TryGetValue(subRecipe.Id, out var cached))
            return cached;

        var result = Compute(subRecipe, memo, visiting);
        if (!result.Issues.Any(i => i.Kind == CostIssueKind.Cycle))
            memo[subRecipe.Id] = result;
        return result;
    }

    private void CollectAllergens(Recipe recipe, HashSet<int> visited, ref EuAllergen allergens)
    {
        foreach (var line in recipe.Lines)
        {
            if (line.IngredientId is { } ingredientId)
            {
                if (_ingredientsById.TryGetValue(ingredientId, out var ingredient))
                    allergens |= ingredient.Allergens;
            }
            else if (line.SubRecipeId is { } subRecipeId
                     && _recipesById.TryGetValue(subRecipeId, out var subRecipe)
                     && visited.Add(subRecipeId))
            {
                CollectAllergens(subRecipe, visited, ref allergens);
            }
        }
    }

    /// <summary>Vrai si <paramref name="container"/> référence <paramref name="targetId"/> par ses lignes, directement ou non.</summary>
    private bool Contains(Recipe container, int targetId, HashSet<int> visited)
    {
        if (!visited.Add(container.Id))
            return false;

        foreach (var line in container.Lines)
        {
            if (line.SubRecipeId is not { } subRecipeId)
                continue;

            if (subRecipeId == targetId)
                return true;

            if (_recipesById.TryGetValue(subRecipeId, out var subRecipe) && Contains(subRecipe, targetId, visited))
                return true;
        }

        return false;
    }

    /// <summary>L'identifiant tient lieu de nom : un élément inconnu n'en a pas.</summary>
    private static CostIssue UnknownItem(int? id, int? lineIndex)
        => new(CostIssueKind.UnknownItem, id is { } value ? $"#{value}" : string.Empty, lineIndex);
}
