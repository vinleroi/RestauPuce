namespace ChefOS.Infrastructure.Payments;

/// <summary>
/// Section <c>Payments</c> : passerelle choisie (<c>Demo</c> par défaut ou <c>Mollie</c>). L'adresse publique de la vitrine (pages de paiement de démonstration,
/// retour du client) est <see cref="PublicSiteOptions"/> (<c>Vitrine:BaseUrl</c>).
/// </summary>
public class PaymentOptions
{
    public const string SectionName = "Payments";
    public const string MollieGateway = "Mollie";

    public string? Gateway { get; set; } = "Demo";
}
