using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Receipts;

/// <summary>Association mémorisée entre un libellé de ticket (normalisé) et un ingrédient ; sans fournisseur, elle vaut pour tous.</summary>
public class SupplierAlias : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int? SupplierId { get; set; }
    public string NormalizedLabel { get; set; } = string.Empty;
    public int IngredientId { get; set; }
}
