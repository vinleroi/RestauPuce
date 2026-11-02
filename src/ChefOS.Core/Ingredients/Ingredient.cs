using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Core.Ingredients;

public class Ingredient : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Divers";
    public EuAllergen Allergens { get; set; } = EuAllergen.None;
    public BaseUnit BaseUnit { get; set; }
    public decimal? CurrentPricePerBaseUnit { get; set; }
    public decimal YieldPercent { get; set; } = 100m;
    public decimal? AveragePieceWeightGrams { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public bool IsArchived { get; set; }
}
