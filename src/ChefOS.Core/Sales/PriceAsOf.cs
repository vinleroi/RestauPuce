using ChefOS.Core.Ingredients;

namespace ChefOS.Core.Sales;

/// <summary>Prix d'un ingrédient en vigueur à une date donnée, d'après l'historique des prix.</summary>
public static class PriceAsOf
{
    /// <summary>
    /// Dernier prix de <paramref name="history"/> dont la date est antérieure à la fin du jour <paramref name="dayUtc"/>
    /// (une entrée à 18 h le jour même compte). À date égale, l'entrée au plus grand <c>Id</c> l'emporte.
    /// Sans entrée, renvoie <paramref name="currentPrice"/>.
    /// </summary>
    public static decimal? At(IEnumerable<IngredientPrice> history, DateTime dayUtc, decimal? currentPrice)
    {
        var endExclusive = dayUtc.Date.AddDays(1);
        IngredientPrice? latest = null;

        foreach (var entry in history)
        {
            if (entry.Date >= endExclusive)
                continue;
            if (latest is null || entry.Date > latest.Date || (entry.Date == latest.Date && entry.Id > latest.Id))
                latest = entry;
        }

        return latest?.PricePerBaseUnit ?? currentPrice;
    }

    /// <summary>
    /// Copies des ingrédients dont <c>CurrentPricePerBaseUnit</c> est remplacé par le prix en vigueur au jour <paramref name="dayUtc"/>
    /// (le prix courant est conservé quand l'ingrédient n'a pas d'historique avant cette date). Les entités d'origine ne sont jamais modifiées.
    /// </summary>
    /// <param name="history">Historique des prix, indexé par <c>IngredientId</c>.</param>
    public static IReadOnlyList<Ingredient> WithPricesAt(
        IEnumerable<Ingredient> ingredients, ILookup<int, IngredientPrice> history, DateTime dayUtc)
    {
        var result = new List<Ingredient>();
        foreach (var ingredient in ingredients)
        {
            var price = At(history[ingredient.Id], dayUtc, ingredient.CurrentPricePerBaseUnit);
            result.Add(CopyWithPrice(ingredient, price));
        }
        return result;
    }

    // Recopie explicite de chaque propriété : toute nouvelle propriété d'Ingredient doit être ajoutée ici
    // (PriceAsOfTests le vérifie par réflexion). La navigation Supplier est partagée, pas clonée.
    private static Ingredient CopyWithPrice(Ingredient source, decimal? price) => new()
    {
        Id = source.Id,
        RestaurantId = source.RestaurantId,
        Name = source.Name,
        Category = source.Category,
        Allergens = source.Allergens,
        BaseUnit = source.BaseUnit,
        CurrentPricePerBaseUnit = price,
        YieldPercent = source.YieldPercent,
        AveragePieceWeightGrams = source.AveragePieceWeightGrams,
        SupplierId = source.SupplierId,
        Supplier = source.Supplier,
        IsArchived = source.IsArchived,
    };
}
