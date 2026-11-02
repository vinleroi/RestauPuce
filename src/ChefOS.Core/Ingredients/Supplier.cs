using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Ingredients;

public class Supplier : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}
