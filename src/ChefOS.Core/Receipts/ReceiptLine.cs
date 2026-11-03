using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Core.Receipts;

public class ReceiptLine : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int ReceiptId { get; set; }
    public int Position { get; set; }
    public string RawLabel { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public MeasureUnit? Unit { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    /// <summary>Taux de TVA en fraction (0.06m pour 6 %).</summary>
    public decimal? VatRate { get; set; }
    public decimal? LineTotalExclVat { get; set; }
    /// <summary>Vrai quand le ticket est en TTC et que le taux de TVA de la ligne est inconnu : le prix hors TVA reste à confirmer.</summary>
    public bool VatToCheck { get; set; }
    public int? IngredientId { get; set; }
    public bool IsIgnored { get; set; }
    public decimal? AppliedPricePerBaseUnit { get; set; }
}
