using ChefOS.Infrastructure.Data;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>Écriture dans la file d'e-mails. Les services n'appellent jamais SMTP : ils ajoutent le message avec le changement qui le provoque.</summary>
public static class EmailOutbox
{
    internal const int AddressMaxLength = 320;
    internal const int SubjectMaxLength = 300;

    /// <summary>
    /// Ajoute la ligne au contexte, sans SaveChanges : l'appelant enregistre dans sa propre transaction (un e-mail n'existe donc que si l'événement a eu lieu).
    /// Le message est dû tout de suite. Un sujet trop long est tronqué plutôt que de faire échouer la transaction de l'événement.
    /// Après le commit, l'appelant peut réveiller le dispatcher avec <see cref="EmailDispatchSignal.Notify"/>.
    /// </summary>
    public static void Enqueue(ChefOsDbContext db, EmailMessage message, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.To);

        var subject = message.Subject ?? string.Empty;
        db.OutgoingEmails.Add(new OutgoingEmail
        {
            Kind = message.Kind,
            RestaurantId = message.RestaurantId,
            To = message.To.Trim(),
            ReplyTo = string.IsNullOrWhiteSpace(message.ReplyTo) ? null : message.ReplyTo.Trim(),
            Subject = subject.Length > SubjectMaxLength ? subject[..SubjectMaxLength] : subject,
            TextBody = message.TextBody,
            HtmlBody = string.IsNullOrWhiteSpace(message.HtmlBody) ? null : message.HtmlBody,
            Status = OutgoingEmailStatus.Pending,
            Attempts = 0,
            CreatedAt = nowUtc,
            NextAttemptAt = nowUtc
        });
    }
}
