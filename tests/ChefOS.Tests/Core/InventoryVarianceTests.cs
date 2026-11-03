using ChefOS.Core.Sales;

namespace ChefOS.Tests.Core;

public class InventoryVarianceTests
{
    private static readonly IReadOnlyDictionary<int, decimal> NoQuantities = new Dictionary<int, decimal>();
    private static readonly IReadOnlyDictionary<int, decimal?> NoPrices = new Dictionary<int, decimal?>();

    [Fact]
    public void Variance_ComputesActualAndVariance()
    {
        // Ouverture 10, achats 5, clôture 4, théorique 9 → réel 11, écart 2 (× 3 € = 6 €).
        var result = InventoryVariance.Compute(
            opening: new Dictionary<int, decimal> { [1] = 10m },
            purchases: new Dictionary<int, decimal> { [1] = 5m },
            closing: new Dictionary<int, decimal> { [1] = 4m },
            theoretical: new Dictionary<int, decimal> { [1] = 9m },
            priceAtClose: new Dictionary<int, decimal?> { [1] = 3m });

        var line = Assert.Single(result);
        Assert.Equal(new VarianceLine(1, 10m, 5m, 4m, 11m, 9m, 2m, 6m), line);
    }

    [Fact]
    public void Variance_MissingKeysAreZero_UnknownPriceNull_SortedByValue()
    {
        var result = InventoryVariance.Compute(
            opening: new Dictionary<int, decimal> { [1] = 10m, [4] = 2m },
            purchases: new Dictionary<int, decimal> { [1] = 5m, [2] = 5m },
            closing: new Dictionary<int, decimal> { [1] = 4m, [4] = 1m },
            theoretical: new Dictionary<int, decimal> { [1] = 9m, [3] = 8m },
            priceAtClose: new Dictionary<int, decimal?>
            {
                [1] = 3m,    // écart +2 × 3 = +6
                [2] = null,  // prix connu comme absent → écart sans valeur
                // 3 : absent du dictionnaire des prix → prix inconnu aussi
                [4] = 10m,   // réel 2 + 0 − 1 = 1, théorique 0 → écart +1 × 10 = +10
            });

        // Triées par valeur décroissante (4 : +10, 1 : +6), puis les valeurs inconnues à la fin (2, 3), par id.
        Assert.Equal([4, 1, 2, 3], result.Select(l => l.IngredientId));

        // 4 : absent des achats et du théorique → 0.
        Assert.Equal(new VarianceLine(4, 2m, 0m, 1m, 1m, 0m, 1m, 10m), result[0]);
        Assert.Equal(new VarianceLine(1, 10m, 5m, 4m, 11m, 9m, 2m, 6m), result[1]);
        // 2 : uniquement des achats → consommation réelle 5, rien de théorique, prix null.
        Assert.Equal(new VarianceLine(2, 0m, 5m, 0m, 5m, 0m, 5m, null), result[2]);
        // 3 : uniquement du théorique → réel 0, écart négatif, prix absent → null.
        Assert.Equal(new VarianceLine(3, 0m, 0m, 0m, 0m, 8m, -8m, null), result[3]);
    }

    [Fact]
    public void Variance_NegativeValuesSortAfterPositiveButBeforeUnknown()
    {
        var result = InventoryVariance.Compute(
            opening: new Dictionary<int, decimal> { [1] = 5m, [2] = 5m, [3] = 5m },
            purchases: NoQuantities,
            closing: new Dictionary<int, decimal> { [1] = 5m, [2] = 5m, [3] = 5m },
            theoretical: new Dictionary<int, decimal> { [1] = 1m, [2] = -1m, [3] = 0m },
            priceAtClose: new Dictionary<int, decimal?> { [1] = 2m, [2] = 2m, [3] = null });

        // 1 : écart −1 × 2 = −2 ; 2 : +1 × 2 = +2 ; 3 : prix inconnu.
        Assert.Equal([2, 1, 3], result.Select(l => l.IngredientId));
    }

    [Fact]
    public void Variance_EqualValues_OrderedByIngredientId()
    {
        var result = InventoryVariance.Compute(
            opening: new Dictionary<int, decimal> { [9] = 1m, [3] = 1m, [5] = 1m },
            purchases: NoQuantities,
            closing: NoQuantities,
            theoretical: NoQuantities,
            priceAtClose: new Dictionary<int, decimal?> { [9] = 2m, [3] = 2m, [5] = 2m });

        Assert.Equal([3, 5, 9], result.Select(l => l.IngredientId));
    }

    [Fact]
    public void Variance_ZeroVarianceWithKnownPrice_HasZeroValueNotNull()
    {
        var result = InventoryVariance.Compute(
            opening: new Dictionary<int, decimal> { [1] = 4m },
            purchases: NoQuantities,
            closing: new Dictionary<int, decimal> { [1] = 1m },
            theoretical: new Dictionary<int, decimal> { [1] = 3m },
            priceAtClose: new Dictionary<int, decimal?> { [1] = 7m });

        var line = Assert.Single(result);
        Assert.Equal(0m, line.VarianceQuantity);
        Assert.Equal(0m, line.VarianceValue);
    }

    [Fact]
    public void Variance_NothingPresent_ReturnsEmpty()
    {
        Assert.Empty(InventoryVariance.Compute(NoQuantities, NoQuantities, NoQuantities, NoQuantities, NoPrices));
        // Un prix seul ne crée pas de ligne : la liste porte sur les ingrédients qui ont des quantités.
        Assert.Empty(InventoryVariance.Compute(
            NoQuantities, NoQuantities, NoQuantities, NoQuantities, new Dictionary<int, decimal?> { [1] = 3m }));
    }
}
