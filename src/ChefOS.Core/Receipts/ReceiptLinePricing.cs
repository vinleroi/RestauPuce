using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Units;

namespace ChefOS.Core.Receipts;

public static class ReceiptLinePricing
{
    /// <summary>
    /// Prix par unité de base d'un ingrédient d'après une ligne de ticket : total hors TVA ÷ quantité convertie dans l'unité de base, sans arrondi.
    /// Contrôles dans l'ordre : ingrédient, quantité et unité (quantité strictement positive), total strictement positif, conversion d'unité.
    /// </summary>
    public static ReceiptLinePrice Compute(decimal? quantity, MeasureUnit? unit, decimal? lineTotalExclVat, Ingredient? ingredient)
    {
        if (ingredient is null)
            return Failed(ReceiptLineIssue.MissingIngredient);

        if (quantity is not > 0m || unit is null)
            return Failed(ReceiptLineIssue.MissingQuantity);

        if (lineTotalExclVat is not > 0m)
            return Failed(ReceiptLineIssue.InvalidTotal);

        var converted = UnitConverter.ToBase(quantity.Value, unit.Value, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
        if (!converted.Success || converted.Value <= 0m)
            return Failed(ReceiptLineIssue.IncompatibleUnit);

        return new ReceiptLinePrice(lineTotalExclVat.Value / converted.Value, converted.Value, ReceiptLineIssue.None);
    }

    /// <summary>
    /// Total hors TVA d'une ligne. Un ticket en HT est renvoyé tel quel ; en TTC, le total est divisé par (1 + taux) quand le taux est connu
    /// (fraction, 0.06m pour 6 %, zéro compris), sinon il est renvoyé tel quel et <paramref name="vatToCheck"/> signale que la TVA reste à confirmer.
    /// Un taux négatif n'est pas un taux connu.
    /// </summary>
    public static decimal? ExclVat(decimal? lineTotal, decimal? vatRate, bool amountsIncludeVat, out bool vatToCheck)
    {
        vatToCheck = false;

        if (!amountsIncludeVat)
            return lineTotal;

        if (vatRate is >= 0m)
            return lineTotal is { } total ? VatCalculator.ExclVat(total, vatRate.Value) : null;

        vatToCheck = true;
        return lineTotal;
    }

    private static ReceiptLinePrice Failed(ReceiptLineIssue issue) => new(null, null, issue);
}
