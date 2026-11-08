using ChefOS.Core.Ingredients;
using ChefOS.Core.Vitrine;

namespace ChefOS.Vitrine.Support;

/// <summary>Libellés français affichés au public.</summary>
public static class Labels
{
    public static string WineColorLabel(WineColor color) => color switch
    {
        WineColor.Rouge => "Rouge",
        WineColor.Blanc => "Blanc",
        WineColor.Rose => "Rosé",
        WineColor.Effervescent => "Effervescent",
        WineColor.Doux => "Doux",
        _ => color.ToString()
    };

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
        _ => allergen.ToString()
    };

    public static string AllergenIcon(EuAllergen allergen) => allergen switch
    {
        EuAllergen.Gluten => "🌾",
        EuAllergen.Crustaceans => "🦐",
        EuAllergen.Eggs => "🥚",
        EuAllergen.Fish => "🐟",
        EuAllergen.Peanuts => "🥜",
        EuAllergen.Soy => "🌱",
        EuAllergen.Milk => "🥛",
        EuAllergen.Nuts => "🌰",
        EuAllergen.Celery => "🥬",
        EuAllergen.Mustard => "🟡",
        EuAllergen.Sesame => "⚪",
        EuAllergen.Sulphites => "🍷",
        EuAllergen.Lupin => "🌼",
        EuAllergen.Molluscs => "🐚",
        _ => "•"
    };

    /// <summary>Les allergènes présents dans <paramref name="value"/>, un par drapeau.</summary>
    public static IEnumerable<EuAllergen> Present(EuAllergen value) =>
        Enum.GetValues<EuAllergen>().Where(a => a != EuAllergen.None && value.HasFlag(a));
}
