using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Core.Sales;

/// <summary>Perte d'un ingrédient ou d'un plat. Exactement un des deux, <c>IngredientId</c> ou <c>RecipeId</c>, est renseigné.</summary>
public class WasteEntry : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    /// <summary>Jour de la perte, en UTC.</summary>
    public DateTime Date { get; set; }
    public int? IngredientId { get; set; }
    public int? RecipeId { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Unité de la quantité ; <c>Piece</c> désigne des portions quand la perte concerne un plat.</summary>
    public MeasureUnit Unit { get; set; }
    public WasteReason Reason { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}
