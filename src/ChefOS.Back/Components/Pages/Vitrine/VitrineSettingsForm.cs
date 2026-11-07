using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Vitrine;

/// <summary>
/// Valeurs saisies dans les réglages de la vitrine. Le prépaiement y est exprimé en pourcentage (30 pour 30 %) ; le service le stocke en fraction.
/// Un nombre vidé n'est pas un zéro : <see cref="ToInput"/> le refuse plutôt que d'enregistrer une valeur que le chef n'a pas écrite.
/// Les bornes sont celles du service.
/// </summary>
public sealed class VitrineSettingsForm
{
    /// <summary>Fuseaux proposés d'office ; tout autre fuseau IANA reste saisissable.</summary>
    public static IReadOnlyList<string> SuggestedTimeZones { get; } =
        ["Europe/Brussels", "Europe/Paris", "Europe/Luxembourg", "Europe/Amsterdam"];

    public bool IsPublished { get; set; }
    public string? Tagline { get; set; }
    public string? AmbianceText { get; set; }

    /// <summary>#RRGGBB, ou vide.</summary>
    public string? AccentColor { get; set; }

    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? PublicEmail { get; set; }
    public string TimeZoneId { get; set; } = SuggestedTimeZones[0];

    public bool ReservationsOpen { get; set; }
    public int? GroupThreshold { get; set; }
    public decimal? DepositPerPerson { get; set; }

    /// <summary>Part de la pré-commande à régler d'avance par un groupe, en % (30 pour 30 %).</summary>
    public decimal? PrepaymentPercent { get; set; }

    public int? FreeCancellationHours { get; set; }
    public int? MinLeadMinutes { get; set; }
    public int? MaxDaysAhead { get; set; }
    public int? MaxPartySize { get; set; }
    public int? PendingPaymentMinutes { get; set; }

    public static VitrineSettingsForm From(VitrineProfile profile, string timeZoneId) => new()
    {
        IsPublished = profile.IsPublished,
        Tagline = profile.Tagline,
        AmbianceText = profile.AmbianceText,
        AccentColor = profile.AccentColor,
        City = profile.City,
        Phone = profile.Phone,
        PublicEmail = profile.PublicEmail,
        TimeZoneId = timeZoneId,
        ReservationsOpen = profile.ReservationsOpen,
        GroupThreshold = profile.GroupThreshold,
        DepositPerPerson = profile.DepositPerPerson,
        PrepaymentPercent = profile.PrepaymentPercent * 100m,
        FreeCancellationHours = profile.FreeCancellationHours,
        MinLeadMinutes = profile.MinLeadMinutes,
        MaxDaysAhead = profile.MaxDaysAhead,
        MaxPartySize = profile.MaxPartySize,
        PendingPaymentMinutes = profile.PendingPaymentMinutes
    };

    /// <exception cref="DomainValidationException">Un nombre est vide, ou le fuseau est vide.</exception>
    public VitrineProfileInput ToInput() => new(
        IsPublished,
        Tagline,
        AmbianceText,
        AccentColor,
        City,
        Phone,
        PublicEmail,
        string.IsNullOrWhiteSpace(TimeZoneId) ? throw new DomainValidationException("Le fuseau horaire est obligatoire") : TimeZoneId.Trim(),
        ReservationsOpen,
        Required(GroupThreshold, "Le seuil de groupe"),
        Required(DepositPerPerson, "L'acompte"),
        Required(PrepaymentPercent, "Le pourcentage de prépaiement") / 100m,
        Required(FreeCancellationHours, "Le délai d'annulation gratuite"),
        Required(MinLeadMinutes, "Le délai minimal avant réservation"),
        Required(MaxDaysAhead, "Le nombre de jours de réservation à l'avance"),
        Required(MaxPartySize, "La taille maximale d'un groupe"),
        Required(PendingPaymentMinutes, "Le délai de paiement"));

    /// <summary>Adresse de la vitrine publique : « {baseUrl}/{slug} » ; null tant que l'un des deux manque.</summary>
    public static string? PublicUrl(string? baseUrl, string? slug)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(slug))
            return null;

        return baseUrl.Trim().TrimEnd('/') + "/" + slug.Trim();
    }

    private static T Required<T>(T? value, string label) where T : struct =>
        value ?? throw new DomainValidationException($"{label} est obligatoire");
}
