using ChefOS.Back.Components.Shared;
using ChefOS.Core.Costing;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;

namespace ChefOS.Back.Components.Pages.Waste;

public enum WasteTargetKind
{
    Ingredient,
    Preparation,
    Dish
}

/// <summary>
/// Ce qu'on peut déclarer perdu : un ingrédient (🥕), une préparation (🍲) ou un plat (🍽️). Porte le nom (un élément archivé garde le sien : un produit
/// retiré de la carte peut encore périmer au frigo) et de quoi proposer les seules unités acceptées par le service.
/// </summary>
/// <param name="BaseUnit">Unité de base d'un ingrédient, unité de production d'une préparation ; sans objet pour un plat, qui se perd en portions.</param>
/// <param name="AveragePieceWeightGrams">Poids moyen d'une pièce d'un ingrédient : il ouvre le passage entre pièces et grammes.</param>
public sealed record WasteTarget(
    WasteTargetKind Kind, int Id, string Name, BaseUnit BaseUnit, decimal? AveragePieceWeightGrams = null, bool IsArchived = false)
{
    /// <summary>Texte affiché dans le champ et dans la liste de suggestions : « 🥕 Carottes », « 🍲 Fond de veau » ou « 🍽️ Waterzooi ».</summary>
    public string Label => Kind switch
    {
        WasteTargetKind.Ingredient => "🥕 ",
        WasteTargetKind.Preparation => "🍲 ",
        _ => "🍽️ "
    } + Name + (IsArchived ? " (archivé)" : "");

    /// <summary>Unités acceptées, l'unité d'office en premier : un plat se perd en portions (<c>Piece</c>) seulement.</summary>
    public IReadOnlyList<MeasureUnit> UnitChoices => Kind == WasteTargetKind.Dish
        ? [MeasureUnit.Piece]
        : Ui.ConvertibleUnits(BaseUnit, Kind == WasteTargetKind.Ingredient ? AveragePieceWeightGrams : null);

    public MeasureUnit DefaultUnit => UnitChoices[0];

    /// <summary>Libellé d'une unité de saisie : pour un plat, la pièce est une portion.</summary>
    public string UnitLabel(MeasureUnit unit) =>
        Kind == WasteTargetKind.Dish && unit == MeasureUnit.Piece ? "portions" : Ui.MeasureUnitLabel(unit);

    /// <summary>Le texte (de la recherche, ou du champ après une sélection : « 🥕 Carottes ») correspond à l'élément, sans tenir compte de la casse ni des accents.</summary>
    public bool Matches(string? text) => Ui.ContainsText(Label, text);

    /// <summary>
    /// Les éléments proposés par l'autocomplétion : ingrédients, puis préparations, puis plats ; dans chaque groupe les éléments actifs par nom,
    /// puis les archivés.
    /// </summary>
    public static IReadOnlyList<WasteTarget> Candidates(RecipeBook book)
    {
        var ingredients = book.Ingredients
            .Select(i => new WasteTarget(WasteTargetKind.Ingredient, i.Id, i.Name, i.BaseUnit, i.AveragePieceWeightGrams, i.IsArchived));
        var preparations = book.Recipes
            .Where(r => r.Kind == RecipeKind.Preparation)
            .Select(r => new WasteTarget(WasteTargetKind.Preparation, r.Id, r.Name, r.OutputUnit ?? BaseUnit.Gram, null, r.IsArchived));
        var dishes = book.Recipes
            .Where(r => r.Kind == RecipeKind.Dish)
            .Select(r => new WasteTarget(WasteTargetKind.Dish, r.Id, r.Name, BaseUnit.Piece, null, r.IsArchived));

        return new[] { ingredients, preparations, dishes }
            .SelectMany(group => group
                .OrderBy(t => t.IsArchived)
                .ThenBy(t => t.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(t => t.Id))
            .ToList();
    }
}
