using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Core.Sales;

public class StockCountLine : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int StockCountId { get; set; }
    public int IngredientId { get; set; }
    /// <summary>Quantité comptée, dans l'unité saisie.</summary>
    public decimal Quantity { get; set; }
    public MeasureUnit Unit { get; set; }
    /// <summary>Quantité comptée convertie dans l'unité de base de l'ingrédient, calculée à l'enregistrement.</summary>
    public decimal BaseQuantity { get; set; }
}
