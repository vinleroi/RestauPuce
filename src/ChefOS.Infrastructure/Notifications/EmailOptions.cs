namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Section <c>Email</c>. <see cref="Sender"/> : <c>Log</c> (par défaut, journalise sans rien envoyer) ou <c>Smtp</c>.
/// Le mot de passe SMTP est un secret : user-secrets en développement, variable d'environnement <c>Email__Smtp__Password</c> en production,
/// jamais dans un appsettings. Choix de l'expéditeur lu à l'enregistrement : redémarrage pour le changer.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public const string LogSender = "Log";
    public const string SmtpSender = "Smtp";

    public string Sender { get; set; } = LogSender;
    /// <summary>Adresse d'expédition de la plateforme (SPF et DKIM du domaine d'envoi), pour tous les e-mails.</summary>
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "ChefOS";
    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary><see cref="None"/> : développement local seulement (Mailpit).</summary>
public enum SmtpSecurity
{
    StartTls,
    SslOnConnect,
    None
}
