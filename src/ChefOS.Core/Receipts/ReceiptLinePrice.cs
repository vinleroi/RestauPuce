namespace ChefOS.Core.Receipts;

/// <summary>Prix d'une ligne de ticket ramené à l'unité de base de l'ingrédient ; non arrondi. Prix et quantité ne sont renseignés que si <paramref name="Issue"/> vaut <see cref="ReceiptLineIssue.None"/>.</summary>
public sealed record ReceiptLinePrice(decimal? PricePerBaseUnit, decimal? BaseQuantity, ReceiptLineIssue Issue);
