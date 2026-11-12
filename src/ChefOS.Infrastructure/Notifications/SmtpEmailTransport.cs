using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Envoi SMTP standard (MailKit) : Brevo, Postmark, Mailjet, OVH, Gmail… ou Mailpit en développement. Une connexion par message (le dispatcher envoie
/// un message à la fois). Le <c>From</c> est toujours l'adresse de la plateforme ; le <c>Reply-To</c> est celui du message.
/// Classement des erreurs :
/// <list type="bullet">
/// <item><see cref="EmailFailure.ServerUnavailable"/> : connexion impossible, TLS ou authentification refusés, délai dépassé, connexion perdue, configuration incomplète ;</item>
/// <item><see cref="EmailFailure.Temporary"/> : une réponse 4xx sur ce message (sauf 421, fermeture du service : panne du serveur) ;</item>
/// <item><see cref="EmailFailure.Permanent"/> : une réponse 5xx sur ce message (dont un destinataire refusé), ou une adresse de destinataire invalide.</item>
/// </list>
/// Les raisons ne reprennent jamais le texte du serveur, qui peut citer l'adresse.
/// </summary>
public sealed class SmtpEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    /// <summary>Délai des opérations réseau de MailKit (lecture, écriture) ; le dispatcher borne en plus chaque envoi à 30 s.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        var host = settings.Smtp.Host;
        // Configuration manquante : panne « du serveur » (les messages attendent qu'elle soit corrigée), pas un refus du message.
        if (string.IsNullOrWhiteSpace(host) || !MailboxAddress.TryParse(settings.FromAddress ?? string.Empty, out var from))
            throw new EmailTransportException("configuration SMTP incomplète (Email:Smtp:Host ou Email:FromAddress)", EmailFailure.ServerUnavailable);
        from.Name = settings.FromName;
        var mime = Build(message, from);

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(host, settings.Smtp.Port, Security(settings.Smtp.Security), ct);
            if (!string.IsNullOrWhiteSpace(settings.Smtp.Username))
                await client.AuthenticateAsync(settings.Smtp.Username, settings.Smtp.Password ?? string.Empty, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new EmailTransportException(ServerReason(ex), EmailFailure.ServerUnavailable, ex);
        }

        try
        {
            await client.SendAsync(mime, ct);
        }
        catch (SmtpCommandException ex)
        {
            var code = (int)ex.StatusCode;
            var failure = code == 421 ? EmailFailure.ServerUnavailable : code >= 500 ? EmailFailure.Permanent : EmailFailure.Temporary;
            throw new EmailTransportException($"SMTP {code} ({ex.ErrorCode})", failure, ex);
        }
        catch (Exception ex) when (ex is NotSupportedException or SmtpUtf8AddressException)
        {
            // Adresse internationalisée refusée par ce serveur : le message ne passera jamais.
            throw new EmailTransportException($"adresse non prise en charge ({ex.GetType().Name})", EmailFailure.Permanent, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new EmailTransportException(ServerReason(ex), EmailFailure.ServerUnavailable, ex);
        }

        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception)
        {
            // Le message est déjà accepté par le serveur : une fermeture ratée (ou annulée) ne doit pas le faire renvoyer.
        }
    }

    private static MimeMessage Build(EmailMessage message, MailboxAddress from)
    {
        if (!MailboxAddress.TryParse(message.To, out var to))
            throw new EmailTransportException("adresse du destinataire invalide", EmailFailure.Permanent);

        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(from);
        mime.To.Add(to);
        // Une adresse publique mal saisie par le restaurant ne doit pas bloquer l'e-mail du client : elle est ignorée.
        if (!string.IsNullOrWhiteSpace(message.ReplyTo) && MailboxAddress.TryParse(message.ReplyTo, out var replyTo))
            mime.ReplyTo.Add(replyTo);

        mime.Body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = string.IsNullOrWhiteSpace(message.HtmlBody) ? null : message.HtmlBody
        }.ToMessageBody();
        return mime;
    }

    private static SecureSocketOptions Security(SmtpSecurity security) => security switch
    {
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls
    };

    private static string ServerReason(Exception ex) => ex switch
    {
        SocketException socket => $"connexion impossible ({socket.SocketErrorCode})",
        SslHandshakeException => "négociation TLS refusée",
        AuthenticationException => "authentification refusée",
        ServiceNotAuthenticatedException => "authentification exigée par le serveur",
        SmtpCommandException command => $"SMTP {(int)command.StatusCode} ({command.ErrorCode})",
        TimeoutException => "délai dépassé",
        OperationCanceledException => "délai dépassé",
        IOException { InnerException: SocketException { SocketErrorCode: SocketError.TimedOut } } => "délai dépassé",
        _ => $"serveur indisponible ({ex.GetType().Name})"
    };
}
