using ChefOS.Core.Ingredients;

namespace ChefOS.Core.Receipts;

public static class IngredientMatcher
{
    private const double MinimumSimilarity = 0.5;
    private const int MinimumTokenLength = 2;

    // Mots vides français : ils ne disent rien de l'ingrédient (« Filet DE bœuf »).
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "A", "AU", "AUX", "DE", "DES", "DU", "EN", "ET", "LA", "LE", "LES"
    };

    /// <summary>
    /// Ingrédient proposé pour un libellé de ticket, avec l'origine de la proposition : d'abord un alias du même fournisseur
    /// (<see cref="MatchSource.SupplierAlias"/>), puis un alias valable pour tous les fournisseurs (<see cref="MatchSource.GlobalAlias"/>) ;
    /// ces alias comparent le libellé normalisé. Enfin, l'ingrédient non archivé le plus proche par nom (<see cref="MatchSource.NameMatch"/>) :
    /// similarité ≥ 0.5, départagée par le plus de mots communs, puis le moins de mots dans le nom de l'ingrédient (le plus précis), puis le plus petit id.
    /// Un alias qui pointe vers un ingrédient archivé ou absent de <paramref name="ingredients"/> est ignoré. Renvoie null sans proposition.
    /// </summary>
    public static IngredientSuggestion? Suggest(string rawLabel, int? supplierId, IReadOnlyList<SupplierAlias> aliases, IReadOnlyList<Ingredient> ingredients)
    {
        var normalized = LabelNormalizer.Normalize(rawLabel);
        if (normalized.Length == 0)
            return null;

        var active = new HashSet<int>();
        foreach (var ingredient in ingredients)
        {
            if (!ingredient.IsArchived)
                active.Add(ingredient.Id);
        }

        if (supplierId is { } supplier
            && FindAlias(aliases, normalized, supplier, active) is { } bySupplier)
            return new IngredientSuggestion(bySupplier, MatchSource.SupplierAlias);

        if (FindAlias(aliases, normalized, null, active) is { } global)
            return new IngredientSuggestion(global, MatchSource.GlobalAlias);

        var labelTokens = Tokens(rawLabel);
        var best = ingredients
            .Where(ingredient => !ingredient.IsArchived)
            .Select(ingredient =>
            {
                var tokens = Tokens(ingredient.Name);
                var (score, common) = Compare(labelTokens, tokens);
                return (ingredient.Id, Score: score, Common: common, TokenCount: tokens.Count);
            })
            .Where(candidate => candidate.Score >= MinimumSimilarity)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Common)
            .ThenBy(candidate => candidate.TokenCount)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => (int?)candidate.Id)
            .FirstOrDefault();

        return best is { } id ? new IngredientSuggestion(id, MatchSource.NameMatch) : null;
    }

    /// <summary>
    /// Part des mots du plus court des deux libellés qui se retrouvent dans l'autre, de 0 à 1. Mots = suites de [A-Z0-9] après normalisation,
    /// de 2 caractères ou plus, sans doublons, hors mots vides français (de, du, la, …) et hors mots contenant un chiffre (« 1KG », « 40 », « T55 »).
    /// Vaut 0 quand l'un des deux libellés n'a aucun mot.
    /// </summary>
    public static double Similarity(string a, string b) => Compare(Tokens(a), Tokens(b)).Score;

    private static (double Score, int Common) Compare(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return (0.0, 0);

        var shorter = a.Count <= b.Count ? a : b;
        var longer = ReferenceEquals(shorter, a) ? b : a;

        var common = shorter.Count(longer.Contains);
        return ((double)common / shorter.Count, common);
    }

    private static int? FindAlias(IReadOnlyList<SupplierAlias> aliases, string normalizedLabel, int? supplierId, HashSet<int> activeIngredientIds)
    {
        foreach (var alias in aliases)
        {
            if (alias.SupplierId == supplierId
                && alias.NormalizedLabel == normalizedLabel
                && activeIngredientIds.Contains(alias.IngredientId))
                return alias.IngredientId;
        }

        return null;
    }

    private static HashSet<string> Tokens(string label)
        => LabelNormalizer.Normalize(label)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= MinimumTokenLength
                            && !StopWords.Contains(token)
                            && !token.Any(char.IsAsciiDigit))
            .ToHashSet(StringComparer.Ordinal);
}
