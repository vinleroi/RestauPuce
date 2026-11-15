namespace ChefOS.Infrastructure;

/// <summary>
/// Section <c>Vitrine</c> : adresse publique du site public, lue par les deux sites (liens des e-mails, retour de paiement, pages de paiement de démonstration,
/// lien affiché dans les réglages du back). Elle remplace l'ancienne <c>Payments:BaseUrl</c>.
/// </summary>
public sealed class PublicSiteOptions
{
    public const string SectionName = "Vitrine";

    public string BaseUrl { get; set; } = "http://localhost:5108";
}
