namespace ChefOS.Core.Costing;

public sealed record LineCost(int LineIndex, decimal? Cost);

/// <summary>
/// Coût de revient d'une recette. Les montants ne sont pas arrondis (arrondi à l'affichage).
/// <see cref="TotalCost"/> ne compte que les lignes chiffrées : tant que <see cref="IsComplete"/> est faux, c'est un coût partiel.
/// Les trois dernières valeurs ne sont renseignées que pour un plat avec prix de vente et taux de TVA.
/// </summary>
public sealed record CostResult(
    decimal TotalCost,
    bool IsComplete,
    IReadOnlyList<CostIssue> Issues,
    IReadOnlyList<LineCost> Lines,
    decimal? PriceExclVat,
    decimal? FoodCostPercent,
    decimal? Margin);
