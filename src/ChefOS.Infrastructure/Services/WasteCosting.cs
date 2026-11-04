using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;

namespace ChefOS.Infrastructure.Services;

/// <summary>Une perte valorisée : le nom de sa cible, son coût (non arrondi, null s'il est inconnu) et le caractère partiel de ce coût.</summary>
internal readonly record struct WasteValuation(string ItemName, bool IsRecipe, decimal? Cost, bool CostIncomplete);

/// <summary>
/// Valorise des pertes aux prix en vigueur à leur date : la règle commune à <see cref="WasteService"/> (liste des pertes) et à <see cref="StatsService"/>
/// (coût des pertes d'une période). Pour un ingrédient, c'est le prix × la quantité convertie en unité de base, sans rendement : on jette le produit tel
/// qu'acheté. Pour un plat, le coût de revient d'une portion × le nombre de portions. Pour une préparation, son coût total ÷ sa quantité produite, par
/// unité de production, × la quantité convertie dans cette unité.
/// Tout est chargé par l'appelant, en une fois ; un livre de recettes est construit par jour de perte et mémorisé (tous les coûts d'une même date
/// partagent les mêmes prix et le même cache de coûts). L'instance n'est pas thread-safe.
/// </summary>
internal sealed class WasteCosting
{
    private readonly IReadOnlyList<Recipe> _recipes;
    private readonly IReadOnlyList<Ingredient> _ingredients;
    private readonly ILookup<int, IngredientPrice> _history;
    private readonly Dictionary<int, Ingredient> _ingredientsById;
    private readonly Dictionary<int, Recipe> _recipesById;
    private readonly Dictionary<DateTime, RecipeBook> _booksByDay = new();

    /// <param name="ingredients">Ingrédients avec leur prix courant (repli de <see cref="PriceAsOf.At"/> sans historique antérieur).</param>
    /// <param name="history">Historique des prix, indexé par <c>IngredientId</c>.</param>
    public WasteCosting(IReadOnlyList<Recipe> recipes, IReadOnlyList<Ingredient> ingredients, ILookup<int, IngredientPrice> history)
    {
        _recipes = recipes;
        _ingredients = ingredients;
        _history = history;
        _ingredientsById = ingredients.ToDictionary(i => i.Id);
        _recipesById = recipes.ToDictionary(r => r.Id);
    }

    /// <summary>La perte valorisée, ou null si sa cible a disparu (supprimée entre deux lectures : la perte part avec elle par cascade).</summary>
    public WasteValuation? Value(WasteEntry entry)
    {
        var day = DateTime.SpecifyKind(entry.Date, DateTimeKind.Utc);

        if (entry.IngredientId is { } ingredientId && _ingredientsById.TryGetValue(ingredientId, out var ingredient))
        {
            var price = PriceAsOf.At(_history[ingredient.Id], day, ingredient.CurrentPricePerBaseUnit);
            return new WasteValuation(ingredient.Name, IsRecipe: false, CostOfIngredient(entry, ingredient, price), CostIncomplete: false);
        }

        if (entry.RecipeId is { } recipeId && _recipesById.TryGetValue(recipeId, out var recipe))
        {
            var result = BookAt(day).Cost(recipe.Id);
            return new WasteValuation(recipe.Name, IsRecipe: true, CostOfRecipe(entry, recipe, result), !result.IsComplete);
        }

        return null;
    }

    /// <summary>La quantité d'une perte d'ingrédient en unité de base (sans rendement), ou null si son unité ne se convertit plus.</summary>
    public static decimal? BaseQuantity(WasteEntry entry, Ingredient ingredient)
    {
        var quantity = UnitConverter.ToBase(entry.Quantity, entry.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
        return quantity.Success ? quantity.Value : null;
    }

    private RecipeBook BookAt(DateTime day)
    {
        if (!_booksByDay.TryGetValue(day, out var book))
        {
            book = new RecipeBook(_recipes, PriceAsOf.WithPricesAt(_ingredients, _history, day));
            _booksByDay[day] = book;
        }
        return book;
    }

    private static decimal? CostOfIngredient(WasteEntry entry, Ingredient ingredient, decimal? pricePerBaseUnit) =>
        BaseQuantity(entry, ingredient) is { } quantity && pricePerBaseUnit is { } price ? quantity * price : null;

    private static decimal? CostOfRecipe(WasteEntry entry, Recipe recipe, CostResult result)
    {
        if (recipe.Kind == RecipeKind.Dish)
            return entry.Unit == MeasureUnit.Piece ? result.TotalCost * entry.Quantity : null;

        if (recipe.OutputQuantity is not { } outputQuantity || outputQuantity <= 0m || recipe.OutputUnit is not { } outputUnit)
            return null;

        var quantity = UnitConverter.ToBase(entry.Quantity, entry.Unit, outputUnit, averagePieceWeightGrams: null);
        return quantity.Success ? quantity.Value * result.TotalCost / outputQuantity : null;
    }
}
