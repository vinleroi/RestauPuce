using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Core.Recipes;

public class Recipe : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public RecipeKind Kind { get; set; }
    public string? Category { get; set; }
    public decimal? SellingPriceInclVat { get; set; }
    public decimal? VatRate { get; set; }
    public decimal? OutputQuantity { get; set; }
    public BaseUnit? OutputUnit { get; set; }
    public string? Instructions { get; set; }
    public bool IsArchived { get; set; }
    public List<RecipeLine> Lines { get; set; } = new();
}
