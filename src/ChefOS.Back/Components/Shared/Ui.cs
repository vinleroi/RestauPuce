using System.Globalization;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Units;

namespace ChefOS.Back.Components.Shared;

/// <summary>
/// Culture et formats d'affichage communs aux pages du back : montants « 12,50 € », pourcentages « 87,5 % »,
/// libellés français des unités et des allergènes. Les arrondis ne servent qu'à l'affichage : les valeurs stockées restent exactes.
/// </summary>
public static class Ui
{
    private const string NoValue = "—";

    /// <summary>
    /// Culture de saisie et d'affichage. <c>MudNumericField</c> prend la culture invariante par défaut (virgule refusée) :
    /// chaque champ numérique doit recevoir <c>Culture="Ui.Fr"</c>.
    /// </summary>
    public static CultureInfo Fr { get; } = CultureInfo.GetCultureInfo("fr-BE");

    /// <summary>« 12,50 € ».</summary>
    public static string Money(decimal amount) => amount.ToString("N2", Fr) + " €";

    /// <summary>Pourcentage depuis une fraction (0,875 → « 87,5 % »), au plus deux décimales, sans zéro inutile.</summary>
    public static string Percent(decimal fraction) => (fraction * 100m).ToString("0.##", Fr) + " %";

    /// <summary>Prix par unité d'affichage (« 12,50 € / kg »), converti depuis le prix par unité de base ; « — » sans prix.</summary>
    public static string PricePerDisplayUnit(decimal? pricePerBaseUnit, BaseUnit baseUnit) =>
        pricePerBaseUnit is { } price
            ? $"{Money(UnitConverter.PriceDisplayFromBase(price, baseUnit))} / {UnitConverter.DisplayUnitLabel(baseUnit)}"
            : NoValue;

    public static string BaseUnitLabel(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => "Gramme",
        BaseUnit.Milliliter => "Millilitre",
        BaseUnit.Piece => "Pièce",
        _ => throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit, null)
    };

    /// <summary>Les 14 allergènes réglementaires de l'UE, un par valeur de l'énumération (hors <c>None</c>).</summary>
    public static IReadOnlyList<EuAllergen> Allergens { get; } =
        Enum.GetValues<EuAllergen>().Where(a => a != EuAllergen.None).ToArray();

    public static string AllergenLabel(EuAllergen allergen) => allergen switch
    {
        EuAllergen.Gluten => "Gluten",
        EuAllergen.Crustaceans => "Crustacés",
        EuAllergen.Eggs => "Œufs",
        EuAllergen.Fish => "Poisson",
        EuAllergen.Peanuts => "Arachides",
        EuAllergen.Soy => "Soja",
        EuAllergen.Milk => "Lait",
        EuAllergen.Nuts => "Fruits à coque",
        EuAllergen.Celery => "Céleri",
        EuAllergen.Mustard => "Moutarde",
        EuAllergen.Sesame => "Sésame",
        EuAllergen.Sulphites => "Sulfites",
        EuAllergen.Lupin => "Lupin",
        EuAllergen.Molluscs => "Mollusques",
        _ => throw new ArgumentOutOfRangeException(nameof(allergen), allergen, "Un seul allergène est attendu.")
    };

    /// <summary>
    /// Écart entre deux graduations d'un axe vertical de graphique : MudChart impose un entier d'au moins 1 (20 par défaut,
    /// inutilisable pour des prix de quelques euros). Vise environ quatre intervalles sur l'étendue des valeurs, arrondis à 1, 2, 5 × 10ⁿ.
    /// </summary>
    public static int AxisTickStep(double min, double max)
    {
        var target = max > min ? (max - min) / 4 : max / 4;
        if (target <= 1)
            return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var normalized = target / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;

        return (int)Math.Min(nice * magnitude, int.MaxValue);
    }

    /// <summary>Recherche de texte sans tenir compte de la casse ni des accents (« echalote » trouve « Échalotes »). Un terme vide correspond à tout.</summary>
    public static bool ContainsText(string text, string? term) =>
        string.IsNullOrWhiteSpace(term)
        || Fr.CompareInfo.IndexOf(text, term.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
