using ChefOS.Infrastructure;
using Microsoft.Extensions.Options;

namespace ChefOS.Vitrine.Support;

/// <summary>
/// Adresses publiques transmises à la passerelle, sur l'adresse de la vitrine (<c>Vitrine:BaseUrl</c>) : retour du client (<c>{BaseUrl}/{slug}/reservation/{jeton}</c>) et webhook
/// (<c>{BaseUrl}/{slug}/webhooks/mollie</c>). En développement, le webhook est vide : Mollie ne peut pas joindre localhost, la passerelle l'omet,
/// et le retour du client relit le statut.
/// </summary>
public sealed class PublicUrls(IOptions<PublicSiteOptions> options, IHostEnvironment environment)
{
    private string Base => options.Value.BaseUrl.TrimEnd('/');

    public string ReservationBase(string slug) => $"{Base}/{Uri.EscapeDataString(slug)}/reservation";

    public string Webhook(string slug) =>
        environment.IsDevelopment() ? string.Empty : $"{Base}/{Uri.EscapeDataString(slug)}/webhooks/mollie";
}
