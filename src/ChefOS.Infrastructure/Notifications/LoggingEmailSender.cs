using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>Remplaçant de l'envoi réel : journalise le message (destinataire, sujet) sans l'envoyer.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("E-mail à {To} : {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
