namespace ChefOS.Core.Costing;

public enum CostIssueKind
{
    MissingPrice,
    IncompatibleUnit,
    InvalidOutputQuantity,
    UnknownItem,
    Cycle,
    NoLines
}

/// <summary>Un problème bloquant le chiffrage complet d'une recette, rattaché à une ligne quand c'est possible.</summary>
public sealed record CostIssue(CostIssueKind Kind, string ItemName, int? LineIndex);
