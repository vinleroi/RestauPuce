namespace ChefOS.Core.Sales;

/// <summary>Écart d'inventaire d'un ingrédient, en unité de base ; <c>VarianceValue</c> est null si le prix est inconnu.</summary>
public record VarianceLine(
    int IngredientId,
    decimal Opening,
    decimal Purchases,
    decimal Closing,
    decimal ActualUse,
    decimal TheoreticalUse,
    decimal VarianceQuantity,
    decimal? VarianceValue);

/// <summary>Écart entre la consommation réelle (mesurée par inventaire) et la consommation théorique (ventes + pertes).</summary>
public static class InventoryVariance
{
    /// <summary>
    /// Un ingrédient absent d'un dictionnaire de quantités compte pour 0 ; chaque ingrédient présent dans au moins
    /// un des quatre dictionnaires de quantités donne une ligne (<paramref name="priceAtClose"/> ne sert qu'à valoriser).
    /// <c>ActualUse = ouverture + achats − clôture</c> ; <c>VarianceQuantity = ActualUse − TheoreticalUse</c> ;
    /// <c>VarianceValue = VarianceQuantity × prix</c> (null si le prix est inconnu ou absent).
    /// Tri par valeur décroissante, valeurs inconnues en dernier, puis par identifiant d'ingrédient.
    /// </summary>
    public static IReadOnlyList<VarianceLine> Compute(
        IReadOnlyDictionary<int, decimal> opening,
        IReadOnlyDictionary<int, decimal> purchases,
        IReadOnlyDictionary<int, decimal> closing,
        IReadOnlyDictionary<int, decimal> theoretical,
        IReadOnlyDictionary<int, decimal?> priceAtClose)
    {
        var ingredientIds = opening.Keys
            .Concat(purchases.Keys)
            .Concat(closing.Keys)
            .Concat(theoretical.Keys)
            .Distinct();

        return ingredientIds
            .Select(id =>
            {
                var open = QuantityOf(opening, id);
                var bought = QuantityOf(purchases, id);
                var close = QuantityOf(closing, id);
                var expected = QuantityOf(theoretical, id);

                var actualUse = open + bought - close;
                var varianceQuantity = actualUse - expected;
                var price = priceAtClose.TryGetValue(id, out var known) ? known : null;

                return new VarianceLine(id, open, bought, close, actualUse, expected, varianceQuantity, varianceQuantity * price);
            })
            .OrderBy(line => line.VarianceValue is null)
            .ThenByDescending(line => line.VarianceValue)
            .ThenBy(line => line.IngredientId)
            .ToList();
    }

    private static decimal QuantityOf(IReadOnlyDictionary<int, decimal> quantities, int ingredientId) =>
        quantities.TryGetValue(ingredientId, out var quantity) ? quantity : 0m;
}
