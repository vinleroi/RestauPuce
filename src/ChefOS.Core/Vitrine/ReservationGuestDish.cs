using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

/// <summary>Plat pré-commandé par un convive ; prix et TVA sont figés à la réservation.</summary>
public class ReservationGuestDish : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int GuestId { get; set; }
    public int RecipeId { get; set; }
    /// <summary>≥ 1.</summary>
    public int Quantity { get; set; } = 1;
    public decimal UnitPriceInclVat { get; set; }
    public decimal VatRate { get; set; }
}
