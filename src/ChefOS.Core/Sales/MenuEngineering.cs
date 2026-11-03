namespace ChefOS.Core.Sales;

public enum MenuClass
{
    /// <summary>Populaire et marge élevée : à mettre en avant.</summary>
    Etoile,
    /// <summary>Populaire mais marge faible : à retravailler (coût, prix).</summary>
    VacheALait,
    /// <summary>Peu vendu mais marge élevée : à promouvoir.</summary>
    Enigme,
    /// <summary>Peu vendu et marge faible : à retirer ou à repenser.</summary>
    PoidsMort
}

/// <param name="UnitMargin">Marge par portion vendue (prix de vente HT − coût).</param>
public record MenuItem(int RecipeId, decimal QuantitySold, decimal UnitMargin);

/// <param name="PopularityShare">Part du plat dans le total des portions vendues (0 à 1).</param>
public record MenuClassification(int RecipeId, MenuClass Class, decimal PopularityShare, decimal UnitMargin);

/// <summary>Classement « menu engineering » (matrice popularité × marge).</summary>
public static class MenuEngineering
{
    /// <summary>Un plat est populaire à partir de cette fraction d'une part égale (1 / n) du total vendu.</summary>
    public const decimal PopularityFactor = 0.70m;

    /// <summary>
    /// Classe les plats vendus. Seuls ceux dont <c>QuantitySold &gt; 0</c> comptent (absents du résultat et de <c>n</c> sinon).
    /// Populaire : part ≥ 0.70 × (1 / n), évalué sans 1 / n (arrondi pour la plupart des n) : qty × n ≥ 0.70 × total, exact en décimal.
    /// Marge élevée : marge unitaire ≥ marge moyenne pondérée par les quantités.
    /// Le résultat suit l'ordre de l'entrée.
    /// </summary>
    public static IReadOnlyList<MenuClassification> Classify(IReadOnlyList<MenuItem> items)
    {
        var sold = items.Where(i => i.QuantitySold > 0).ToList();
        if (sold.Count == 0)
            return [];

        var totalQuantity = sold.Sum(i => i.QuantitySold);
        // part ≥ 0.70 / n  ⇔  qty × n ≥ 0.70 × total : aucun quotient arrondi, l'égalité au seuil reste exacte.
        var popularityFloor = PopularityFactor * totalQuantity;
        var marginThreshold = sold.Sum(i => i.QuantitySold * i.UnitMargin) / totalQuantity;

        return sold
            .Select(item =>
            {
                var share = item.QuantitySold / totalQuantity;
                var popular = item.QuantitySold * sold.Count >= popularityFloor;
                var highMargin = item.UnitMargin >= marginThreshold;
                var menuClass = (popular, highMargin) switch
                {
                    (true, true) => MenuClass.Etoile,
                    (true, false) => MenuClass.VacheALait,
                    (false, true) => MenuClass.Enigme,
                    (false, false) => MenuClass.PoidsMort,
                };
                return new MenuClassification(item.RecipeId, menuClass, share, item.UnitMargin);
            })
            .ToList();
    }
}
