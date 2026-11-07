using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

public class Reservation : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    /// <summary>Jeton public (32+ caractères url-safe) pour consulter/annuler sans compte.</summary>
    public string PublicToken { get; set; } = string.Empty;
    public DateTime StartsAtUtc { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public int PartySize { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string? Note { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public decimal AmountDue { get; set; }
    public decimal PreorderTotalInclVat { get; set; }
    public int? OrderId { get; set; }
    public bool IsManual { get; set; }
    public List<ReservationGuest> Guests { get; set; } = new();
}
