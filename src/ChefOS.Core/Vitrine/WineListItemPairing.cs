using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

public class WineListItemPairing : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int WineListItemId { get; set; }
    public int RecipeId { get; set; }
}
