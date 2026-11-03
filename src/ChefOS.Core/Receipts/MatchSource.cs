namespace ChefOS.Core.Receipts;

/// <summary>D'où vient la proposition d'ingrédient d'une ligne de ticket : un alias mémorisé est fiable, un rapprochement par nom reste une supposition.</summary>
public enum MatchSource
{
    SupplierAlias,
    GlobalAlias,
    NameMatch
}
