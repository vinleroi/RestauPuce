using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

public class ReservationGuest : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int ReservationId { get; set; }
    public int Position { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public List<ReservationGuestDish> Dishes { get; set; } = new();
}
