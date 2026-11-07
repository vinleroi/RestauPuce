using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Remplaçant de l'envoi réel : journalise le destinataire masqué (<c>j***@d***.be</c>) et le sujet. Le corps n'est écrit qu'au niveau Debug
/// (il contient des données personnelles).
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("E-mail à {To} : {Subject}", Mask(to), subject);
        logger.LogDebug("Corps de l'e-mail à {To} : {Body}", Mask(to), body);
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
