using System.Globalization;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Ce qu'il faut pour écrire les e-mails d'une réservation, déjà dans le fuseau du restaurant. <paramref name="AmountPaid"/> : montant réglé (0 sans paiement) ;
/// <paramref name="RefundAmount"/> : montant remboursé ou en cours de remboursement (0 sinon). <paramref name="PublicEmail"/>, <paramref name="Phone"/> et
/// <paramref name="Address"/> sont ceux que le restaurant affiche au public, omis s'ils sont vides.
/// </summary>
public sealed record ReservationEmailContext(
    string RestaurantName, string? AccentColor, string? PublicEmail, string? Phone, string? Address, string Slug, string BaseUrl,
    string CustomerName, string CustomerEmail, string Token, DateOnly LocalDate, TimeOnly LocalTime, string ServiceName, int PartySize,
    decimal AmountPaid, decimal RefundAmount, DateTime FreeCancellationUntilLocal, int RestaurantId);

/// <summary>
/// Les quatre e-mails aux clients de la vitrine (spec § 3.3), en français : confirmation, annulation par le client, annulation par le restaurant, paiement
/// tardif remboursé. Le <c>Reply-To</c> est l'adresse publique du restaurant quand elle existe : le client qui répond écrit au restaurant.
/// </summary>
public static class ReservationEmails
{
    private static readonly CultureInfo FrBe = CultureInfo.GetCultureInfo("fr-BE");

    public static EmailMessage Confirmed(ReservationEmailContext c)
    {
        var paragraphs = new List<string>
        {
            $"Bonjour {c.CustomerName.Trim()},",
            $"Votre table chez {c.RestaurantName} est réservée : {Covers(c.PartySize)}, le {When(c)} ({c.ServiceName})."
        };
        if (c.AmountPaid > 0m)
            paragraphs.Add($"Montant réglé : {Money(c.AmountPaid)}.");
        var deadline = Deadline(c);
        paragraphs.Add(c.AmountPaid > 0m
            ? $"Annulation gratuite jusqu'au {deadline} : le montant réglé vous est alors remboursé. Ensuite, il n'est plus remboursé."
            : $"Annulation gratuite jusqu'au {deadline}.");
        paragraphs.Add("Pour revoir votre réservation ou l'annuler, utilisez le lien ci-dessous.");
        AddContact(paragraphs, c);

        return Build(EmailKind.ReservationConfirmed, c, $"Votre réservation chez {c.RestaurantName} est confirmée", paragraphs,
            ("Voir ou annuler ma réservation", ReservationUrl(c)));
    }

    public static EmailMessage CancelledByCustomer(ReservationEmailContext c)
    {
        var paragraphs = new List<string>
        {
            $"Bonjour {c.CustomerName.Trim()},",
            $"Nous avons bien enregistré l'annulation de votre réservation chez {c.RestaurantName} du {When(c)} ({c.ServiceName}, {Covers(c.PartySize)})."
        };
        if (c.RefundAmount > 0m)
            paragraphs.Add($"Votre paiement de {Money(c.RefundAmount)} vous est remboursé ; le remboursement apparaît sur votre compte d'ici quelques jours.");
        else if (c.AmountPaid > 0m)
            paragraphs.Add($"L'annulation est intervenue après la limite d'annulation gratuite ({Deadline(c)}) : elle est faite sans remboursement du montant réglé ({Money(c.AmountPaid)}).");
        AddContact(paragraphs, c);

        return Build(EmailKind.ReservationCancelledByCustomer, c, $"Votre réservation chez {c.RestaurantName} est annulée", paragraphs,
            ("Voir ma réservation", ReservationUrl(c)));
    }

    public static EmailMessage CancelledByRestaurant(ReservationEmailContext c)
    {
        var paragraphs = new List<string>
        {
            $"Bonjour {c.CustomerName.Trim()},",
            $"{c.RestaurantName} a dû annuler votre réservation du {When(c)} ({c.ServiceName}, {Covers(c.PartySize)}). Nous en sommes désolés."
        };
        if (c.RefundAmount > 0m)
            paragraphs.Add($"Votre paiement de {Money(c.RefundAmount)} vous est remboursé ; le remboursement apparaît sur votre compte d'ici quelques jours.");
        paragraphs.Add("Pour toute question ou pour convenir d'une autre date, contactez le restaurant.");
        AddContact(paragraphs, c);

        return Build(EmailKind.ReservationCancelledByRestaurant, c, $"{c.RestaurantName} a annulé votre réservation", paragraphs,
            ("Voir ma réservation", ReservationUrl(c)));
    }

    public static EmailMessage LatePaymentRefunded(ReservationEmailContext c)
    {
        var amount = c.RefundAmount > 0m ? c.RefundAmount : c.AmountPaid;
        var paragraphs = new List<string>
        {
            $"Bonjour {c.CustomerName.Trim()},",
            $"Votre paiement de {Money(amount)} pour la table du {When(c)} ({c.ServiceName}, {Covers(c.PartySize)}) chez {c.RestaurantName} est arrivé trop tard : "
            + "le délai de paiement était dépassé et la place a été reprise entre-temps. La réservation n'est donc pas confirmée.",
            "Nous vous remboursons intégralement ; le remboursement apparaît sur votre compte d'ici quelques jours.",
            "Vous pouvez faire une nouvelle réservation avec le lien ci-dessous."
        };
        AddContact(paragraphs, c);

        return Build(EmailKind.LatePaymentRefunded, c, $"Votre paiement à {c.RestaurantName} a été remboursé", paragraphs,
            ("Réserver à nouveau", $"{SiteBase(c)}/reserver"));
    }

    /// <summary>Le lien personnel de la réservation : <c>{BaseUrl sans / final}/{slug échappé}/reservation/{jeton}</c>.</summary>
    public static string ReservationUrl(ReservationEmailContext c) => $"{SiteBase(c)}/reservation/{Uri.EscapeDataString(c.Token)}";

    private static string SiteBase(ReservationEmailContext c) => $"{c.BaseUrl.Trim().TrimEnd('/')}/{Uri.EscapeDataString(c.Slug)}";

    private static EmailMessage Build(EmailKind kind, ReservationEmailContext c, string subject, IReadOnlyList<string> paragraphs, (string Label, string Url) button)
    {
        var (text, html) = EmailLayout.Render(c.RestaurantName, c.AccentColor, paragraphs, button);
        return new EmailMessage(kind, c.CustomerEmail.Trim(), EmailLayout.CleanSubject(subject), text, html, Present(c.PublicEmail), c.RestaurantId);
    }

    /// <summary>Adresse, téléphone et réponse possible au restaurant : seulement ce qui est renseigné.</summary>
    private static void AddContact(List<string> paragraphs, ReservationEmailContext c)
    {
        var lines = new List<string>();
        if (Present(c.Address) is { } address)
            lines.Add($"Adresse : {address}");
        if (Present(c.Phone) is { } phone)
            lines.Add($"Téléphone : {phone}");
        if (lines.Count > 0)
            paragraphs.Add($"{c.RestaurantName}\n{string.Join('\n', lines)}");
        if (Present(c.PublicEmail) is not null)
            paragraphs.Add("Pour écrire au restaurant, répondez simplement à cet e-mail.");
    }

    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>« vendredi 13/11/2026 à 19:30 ».</summary>
    private static string When(ReservationEmailContext c) =>
        $"{c.LocalDate.ToString("dddd", FrBe)} {c.LocalDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} à {c.LocalTime.ToString("HH\\:mm", CultureInfo.InvariantCulture)}";

    /// <summary>« 12/11/2026 à 19:30 » : la limite d'annulation gratuite figée sur la réservation.</summary>
    private static string Deadline(ReservationEmailContext c) =>
        $"{c.FreeCancellationUntilLocal.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} à {c.FreeCancellationUntilLocal.ToString("HH\\:mm", CultureInfo.InvariantCulture)}";

    private static string Covers(int count) => count == 1 ? "1 couvert" : $"{count} couverts";

    /// <summary>« 40,00 € » (espace insécable), comme les messages des services.</summary>
    private static string Money(decimal amount) => amount.ToString("0.00", FrBe) + " €";
}
