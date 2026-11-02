using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Ingredients;

public class IngredientPrice : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int IngredientId { get; set; }
    public DateTime Date { get; set; }
    public decimal PricePerBaseUnit { get; set; }
    public int? SupplierId { get; set; }
    public PriceSource Source { get; set; }
}
