using ChefOS.Core.Ingredients;

namespace ChefOS.Core.Receipts;

public static class IngredientMatcher
{
    private const double MinimumSimilarity = 0.5;
    private const int MinimumTokenLength = 2;

    /// <summary>
    /// Ingrédient proposé pour un libellé de ticket : d'abord un alias du même fournisseur, puis un alias valable pour tous les fournisseurs
    /// (ces alias comparent le libellé normalisé), enfin l'ingrédient non archivé le plus proche par nom (similarité ≥ 0.5, égalité → plus petit id).
    /// Un alias qui pointe vers un ingrédient archivé ou absent de <paramref name="ingredients"/> est ignoré. Renvoie null sans proposition.
    /// </summary>
    public static int? Suggest(string rawLabel, int? supplierId, IReadOnlyList<SupplierAlias> aliases, IReadOnlyList<Ingredient> ingredients)
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

        if (supplierId is { } supplier)
        {
            var bySupplier = FindAlias(aliases, normalized, supplier, active);
            if (bySupplier is not null)
                return bySupplier;
        }

        var global = FindAlias(aliases, normalized, null, active);
        if (global is not null)
            return global;

        int? best = null;
        var bestScore = 0.0;
        foreach (var ingredient in ingredients)
        {
            if (ingredient.IsArchived)
                continue;

            var score = Similarity(rawLabel, ingredient.Name);
            if (score < MinimumSimilarity)
                continue;

            if (score > bestScore || (score == bestScore && ingredient.Id < best))
            {
                best = ingredient.Id;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Part des mots du plus court des deux libellés qui se retrouvent dans l'autre, de 0 à 1. Mots = suites de [A-Z0-9] après normalisation,
    /// de 2 caractères ou plus, sans doublons. Vaut 0 quand l'un des deux libellés n'a aucun mot.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var tokensA = Tokens(a);
        var tokensB = Tokens(b);
        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0.0;

        var shorter = tokensA.Count <= tokensB.Count ? tokensA : tokensB;
        var longer = ReferenceEquals(shorter, tokensA) ? tokensB : tokensA;

        var common = shorter.Count(longer.Contains);
        return (double)common / shorter.Count;
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
            .Where(token => token.Length >= MinimumTokenLength)
            .ToHashSet(StringComparer.Ordinal);
}
