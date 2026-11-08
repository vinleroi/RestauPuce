namespace ChefOS.Vitrine.Support;

/// <summary>Titre et description des pages publiques d'un restaurant (balises title, description et Open Graph).</summary>
public static class Seo
{
    private const int MaxDescription = 155;

    /// <summary>« {Nom} — {accroche} », ou le nom seul sans accroche.</summary>
    public static string Title(string name, string? tagline) =>
        string.IsNullOrWhiteSpace(tagline) ? name : $"{name} — {tagline.Trim()}";

    /// <summary>L'accroche, à défaut un extrait de l'ambiance (coupé à un mot, 155 caractères au plus), à défaut le nom et la ville.</summary>
    public static string Description(string name, string? city, string? tagline, string? ambiance)
    {
        if (!string.IsNullOrWhiteSpace(tagline))
            return Excerpt(tagline);
        if (!string.IsNullOrWhiteSpace(ambiance))
            return Excerpt(ambiance);
        return string.IsNullOrWhiteSpace(city) ? name : $"{name} à {city.Trim()}";
    }

    public static string Excerpt(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= MaxDescription)
            return flat;

        var cut = flat[..MaxDescription];
        var space = cut.LastIndexOf(' ');
        return (space > MaxDescription / 2 ? cut[..space] : cut).TrimEnd(',', ';', ':', '.', ' ') + "…";
    }
}
