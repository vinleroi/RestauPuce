using ChefOS.Core.Costing;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Back.Components.Pages.Recipes;

public enum RecipeItemKind
{
    Ingredient,
    Preparation
}

/// <summary>
/// Ce qu'une ligne de recette peut contenir : un ingrédient ou une préparation. Porte le nom (un élément archivé garde le sien)
/// et l'unité de base, qui sert à proposer l'unité de saisie la plus naturelle.
/// </summary>
public sealed record RecipeItem(RecipeItemKind Kind, int Id, string Name, BaseUnit BaseUnit, bool IsArchived = false)
{
    /// <summary>Texte affiché dans le champ et dans la liste de suggestions : « 🥕 Carottes » ou « 🍲 Fond de veau ».</summary>
    public string Label =>
        (Kind == RecipeItemKind.Ingredient ? "🥕 " : "🍲 ") + Name + (IsArchived ? " (archivé)" : "");

    /// <summary>
    /// Éléments proposés par l'autocomplétion : ingrédients puis préparations non archivés, chaque groupe trié par nom,
    /// sans la recette en cours d'édition. Un plat n'est jamais proposé : il ne peut pas servir de sous-recette.
    /// </summary>
    public static IReadOnlyList<RecipeItem> Candidates(RecipeBook book, int? excludeRecipeId)
    {
        var ingredients = book.Ingredients
            .Where(i => !i.IsArchived)
            .OrderBy(i => i.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(i => i.Id)
            .Select(i => new RecipeItem(RecipeItemKind.Ingredient, i.Id, i.Name, i.BaseUnit));

        var preparations = book.Recipes
            .Where(r => r.Kind == RecipeKind.Preparation && !r.IsArchived && r.Id != excludeRecipeId)
            .OrderBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(r => r.Id)
            .Select(ToItem);

        return ingredients.Concat(preparations).ToList();
    }

    internal static RecipeItem ToItem(Recipe preparation) =>
        new(RecipeItemKind.Preparation, preparation.Id, preparation.Name, preparation.OutputUnit ?? BaseUnit.Gram, preparation.IsArchived);
}
