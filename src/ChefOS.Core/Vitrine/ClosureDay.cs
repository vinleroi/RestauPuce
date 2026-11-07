using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

public class ClosureDay : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public DateOnly DateLocal { get; set; }
    public string? Reason { get; set; }
}
