namespace ChefOS.Infrastructure.Notifications;

/// <summary>Envoi effectif d'un e-mail (SMTP ou journal). Appelé seulement par <see cref="OutgoingEmailDispatcher"/>, un message à la fois.</summary>
public interface IEmailTransport
{
    /// <summary>
    /// Envoie le message. Un échec est une <see cref="EmailTransportException"/> ; l'annulation par <paramref name="ct"/> (arrêt du site ou délai de 30 s
    /// du dispatcher) remonte telle quelle en <see cref="OperationCanceledException"/>.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public enum EmailFailure
{
    /// <summary>Le serveur ne répond pas (connexion, TLS, authentification, délai) : panne du serveur, pas du message ; aucun essai n'est compté.</summary>
    ServerUnavailable,
    /// <summary>Refus temporaire de ce message (réponse SMTP 4xx) : nouvel essai plus tard.</summary>
    Temporary,
    /// <summary>Refus définitif de ce message (réponse SMTP 5xx, destinataire refusé, adresse invalide) : abandon.</summary>
    Permanent
}

/// <summary>
/// Échec d'envoi classé. <paramref name="reason"/> finit dans <c>OutgoingEmail.LastError</c> et dans les journaux : code et nature de l'erreur seulement,
/// jamais d'adresse ni de corps (ne pas y recopier le texte de la réponse du serveur).
/// </summary>
public sealed class EmailTransportException(string reason, EmailFailure failure, Exception? inner = null) : Exception(reason, inner)
{
    public EmailFailure Failure { get; } = failure;
}
