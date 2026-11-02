using ChefOS.Core.Ingredients;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Ingredients;

/// <summary>
/// Valeurs saisies dans la fiche ingrédient. Le prix y est exprimé par unité d'affichage (€/kg, €/l ou €/pièce) ;
/// la conversion en prix par unité de base se fait sans arrondi, dans <see cref="ToInput"/>.
/// </summary>
public sealed class IngredientForm
{
    public string Name { get; set; } = "";

    public string? Category { get; set; }

    public BaseUnit BaseUnit { get; set; } = BaseUnit.Gram;

    /// <summary>Null : pas de prix saisi (un ingrédient existant conserve alors son prix courant).</summary>
    public decimal? DisplayPrice { get; set; }

    /// <summary>Pourcentage de 1 à 100. Un champ vidé vaut 0 et est refusé par le service avec son message habituel.</summary>
    public decimal? YieldPercent { get; set; } = 100m;

    public decimal? AveragePieceWeightGrams { get; set; }

    public int? SupplierId { get; set; }

    public EuAllergen Allergens { get; set; } = EuAllergen.None;

    public static IngredientForm From(Ingredient ingredient) => new()
    {
        Name = ingredient.Name,
        Category = ingredient.Category,
        BaseUnit = ingredient.BaseUnit,
        DisplayPrice = ingredient.CurrentPricePerBaseUnit is { } price
            ? UnitConverter.PriceDisplayFromBase(price, ingredient.BaseUnit)
            : null,
        YieldPercent = ingredient.YieldPercent,
        AveragePieceWeightGrams = ingredient.AveragePieceWeightGrams,
        SupplierId = ingredient.SupplierId,
        Allergens = ingredient.Allergens
    };

    public IngredientInput ToInput(int? id) => new(
        id,
        Name,
        Category ?? "",
        Allergens,
        BaseUnit,
        DisplayPrice is { } displayPrice ? UnitConverter.PricePerBaseFromDisplay(displayPrice, BaseUnit) : null,
        YieldPercent ?? 0m,
        AveragePieceWeightGrams,
        SupplierId);

    /// <summary>
    /// Change l'unité de base. Le prix affiché était saisi pour l'ancienne unité : il est vidé pour être ressaisi dans la
    /// nouvelle (le service exige alors un prix). Sans effet si l'unité ne change pas.
    /// </summary>
    public void ChangeUnit(BaseUnit unit)
    {
        if (unit == BaseUnit)
            return;

        BaseUnit = unit;
        DisplayPrice = null;
    }

    public bool HasAllergen(EuAllergen allergen) => Allergens.HasFlag(allergen);

    public void SetAllergen(EuAllergen allergen, bool present) =>
        Allergens = present ? Allergens | allergen : Allergens & ~allergen;
}
