namespace ChefOS.Infrastructure.Notifications;

/// <summary>Type d'e-mail, pour le suivi et les journaux.</summary>
public enum EmailKind
{
    ReservationConfirmed,
    ReservationCancelledByCustomer,
    ReservationCancelledByRestaurant,
    LatePaymentRefunded,
    AccountConfirmation,
    PasswordReset
}

public enum OutgoingEmailStatus
{
    Pending,
    Sent,
    Abandoned
}

/// <summary>
/// Un e-mail de la file (table globale <c>OutgoingEmails</c>, sans filtre de restaurant, exposée par aucune page). Il est écrit par
/// <see cref="EmailOutbox.Enqueue"/> dans la transaction de l'événement, puis envoyé par <see cref="OutgoingEmailDispatcher"/>.
/// Le texte et le HTML sont effacés dès l'envoi ou l'abandon ; la ligne est supprimée au bout de 30 jours.
/// </summary>
public class OutgoingEmail
{
    public long Id { get; set; }
    public EmailKind Kind { get; set; }
    /// <summary>Restaurant à l'origine du message, pour le suivi (sans clé étrangère : la file ne bloque rien).</summary>
    public int? RestaurantId { get; set; }
    public string To { get; set; } = string.Empty;
    public string? ReplyTo { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public OutgoingEmailStatus Status { get; set; } = OutgoingEmailStatus.Pending;
    /// <summary>Envois tentés (réussi compris). Une panne du serveur ne compte pas.</summary>
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    /// <summary>Fin du bail d'un dispatcher qui a réservé le message ; passé ce moment, un autre peut le reprendre.</summary>
    public DateTime? LockedUntil { get; set; }
    public DateTime? SentAt { get; set; }
    /// <summary>Dernière erreur : type d'échec et code SMTP, jamais d'adresse ni de corps.</summary>
    public string? LastError { get; set; }
}
