using ChefOS.Infrastructure.Data;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>Écriture dans la file d'e-mails. Les services n'appellent jamais SMTP : ils ajoutent le message avec le changement qui le provoque.</summary>
public static class EmailOutbox
{
    internal const int AddressMaxLength = 320;
    internal const int SubjectMaxLength = 300;

    /// <summary>
    /// Ajoute la ligne au contexte, sans SaveChanges : l'appelant enregistre dans sa propre transaction (un e-mail n'existe donc que si l'événement a eu lieu).
    /// Le message est dû tout de suite. Rien ici ne doit faire échouer la transaction de l'événement :
    /// <list type="bullet">
    /// <item>le sujet est mis sur une ligne et tronqué à 300 caractères (sans couper une paire de substitution) ;</item>
    /// <item>un <c>Reply-To</c> de plus de 320 caractères est ignoré (le tronquer en ferait une autre adresse, ou une adresse invalide) ;</item>
    /// <item>un destinataire de plus de 320 caractères ne peut pas être joint : le message n'est pas ajouté, et la méthode renvoie faux ;</item>
    /// <item><paramref name="nowUtc"/> est ramené en UTC (une heure locale est convertie, une heure sans genre est lue comme UTC).</item>
    /// </list>
    /// Après le commit, l'appelant peut réveiller le dispatcher avec <see cref="EmailDispatchSignal.Notify"/>.
    /// </summary>
    /// <returns>Vrai si le message a été ajouté.</returns>
    public static bool Enqueue(ChefOsDbContext db, EmailMessage message, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.To);

        var to = message.To.Trim();
        if (to.Length > AddressMaxLength)
            return false;

        var replyTo = message.ReplyTo?.Trim();
        if (string.IsNullOrEmpty(replyTo) || replyTo.Length > AddressMaxLength)
            replyTo = null;

        var now = nowUtc.Kind switch
        {
            DateTimeKind.Utc => nowUtc,
            DateTimeKind.Local => nowUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)
        };

        db.OutgoingEmails.Add(new OutgoingEmail
        {
            Kind = message.Kind,
            RestaurantId = message.RestaurantId,
            To = to,
            ReplyTo = replyTo,
            Subject = Truncate(EmailLayout.CleanSubject(message.Subject ?? string.Empty), SubjectMaxLength),
            TextBody = message.TextBody,
            HtmlBody = string.IsNullOrWhiteSpace(message.HtmlBody) ? null : message.HtmlBody,
            Status = OutgoingEmailStatus.Pending,
            Attempts = 0,
            CreatedAt = now,
            NextAttemptAt = now
        });
        return true;
    }

    /// <summary>Au plus <paramref name="max"/> caractères, sans laisser la première moitié d'une paire de substitution (refusée à l'écriture en UTF-8).</summary>
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
            return value;
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut].TrimEnd();
    }
}
