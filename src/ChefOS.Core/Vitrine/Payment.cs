using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

public class Payment : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int ReservationId { get; set; }
    public PaymentKind Kind { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string? GatewayPaymentId { get; set; }
    public string? CheckoutUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? RefundedAt { get; set; }
    public decimal? RefundAmount { get; set; }
}
