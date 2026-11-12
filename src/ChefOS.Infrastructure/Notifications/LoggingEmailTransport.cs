using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Transport sans envoi (<c>Email:Sender = Log</c>, par défaut et dans les tests) : journalise le type, le destinataire masqué (<c>j***@d***.be</c>)
/// et le sujet. Le corps n'est écrit qu'au niveau Debug (il contient des données personnelles).
/// </summary>
public sealed class LoggingEmailTransport(ILogger<LoggingEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation("E-mail {Kind} à {To} : {Subject} (journal, non envoyé)", message.Kind, Mask(message.To), message.Subject);
        logger.LogDebug("Corps de l'e-mail à {To} : {Body}", Mask(message.To), message.TextBody);
        return Task.CompletedTask;
    }

    internal static string Mask(string? address)
    {
        var value = address?.Trim() ?? "";
        var at = value.LastIndexOf('@');
        if (at < 1 || at == value.Length - 1)
            return "***";

        var domain = value[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var host = dot > 0 ? domain[..dot] : domain;
        var tld = dot > 0 ? domain[dot..] : "";
        return $"{value[0]}***@{(host.Length > 0 ? host[0] : '*')}***{tld}";
    }
}
