namespace ChefOS.Core.Receipts;

public enum ReceiptLineIssue
{
    None,
    MissingIngredient,
    MissingQuantity,
    IncompatibleUnit,
    InvalidTotal
}
