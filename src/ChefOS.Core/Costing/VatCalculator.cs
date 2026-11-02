namespace ChefOS.Core.Costing;

public static class VatCalculator
{
    /// <summary>Prix hors TVA à partir d'un prix TTC ; <paramref name="rate"/> est une fraction (0.12m pour 12 %).</summary>
    public static decimal ExclVat(decimal inclVat, decimal rate) => inclVat / (1m + rate);
}
