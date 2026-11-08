namespace ChefOS.Infrastructure.Payments;

/// <summary>
/// Paiement de la passerelle de démonstration, en base (table globale : sans restaurant, partagée entre le back et la vitrine).
/// <see cref="RefundKeys"/> garde les clés d'idempotence des remboursements déjà appliqués.
/// </summary>
public class DemoGatewayPayment
{
    public string Id { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string RedirectUrl { get; set; } = string.Empty;
    public GatewayStatus Status { get; set; } = GatewayStatus.Open;
    public decimal RefundedAmount { get; set; }
    public List<string> RefundKeys { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
