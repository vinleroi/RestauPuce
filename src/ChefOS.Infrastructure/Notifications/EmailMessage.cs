namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Un e-mail à envoyer. <paramref name="HtmlBody"/> vide : texte seul. <paramref name="ReplyTo"/> : l'adresse publique du restaurant, quand elle existe.
/// </summary>
public sealed record EmailMessage(EmailKind Kind, string To, string Subject, string TextBody, string HtmlBody, string? ReplyTo = null, int? RestaurantId = null);
