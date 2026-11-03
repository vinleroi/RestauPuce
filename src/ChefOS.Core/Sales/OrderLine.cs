using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Sales;

public class OrderLine : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int OrderId { get; set; }
    public int RecipeId { get; set; }
    /// <summary>Nombre de portions commandées.</summary>
    public int Quantity { get; set; }
    /// <summary>Prix de vente figé à la prise de commande.</summary>
    public decimal UnitPriceInclVat { get; set; }
    /// <summary>Taux de TVA en fraction (0.12m pour 12 %).</summary>
    public decimal VatRate { get; set; }
    public string? Note { get; set; }
}
