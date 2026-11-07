namespace ChefOS.Infrastructure.Payments;

/// <summary>Section <c>Payments</c> : passerelle choisie (<c>Demo</c> par défaut ou <c>Mollie</c>) et adresse publique de la vitrine (pages de paiement de démonstration).</summary>
public class PaymentOptions
{
    public const string SectionName = "Payments";
    public const string MollieGateway = "Mollie";

    public string? Gateway { get; set; } = "Demo";

    public string BaseUrl { get; set; } = "http://localhost:5108";
}
