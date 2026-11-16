using ChefOS.Infrastructure.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MimeKit;

namespace ChefOS.Infrastructure.Hosting;

/// <summary>
/// Vérifie au démarrage que la configuration d'un site hors <c>Development</c> est complète : un oubli doit empêcher le démarrage,
/// avec un message en français qui dit quoi régler, plutôt que d'échouer à la première réservation ou au premier e-mail.
/// Les messages ne contiennent jamais de valeur de configuration (ni clé, ni mot de passe).
/// </summary>
public static class ProductionConfiguration
{
    public const string MessagePrefix = "Configuration de production incomplète :";

    /// <summary>Les problèmes de configuration, un message en français par problème. Liste vide en <c>Development</c>.</summary>
    public static IReadOnlyList<string> Problems(IConfiguration configuration, IHostEnvironment environment)
    {
        var problems = new List<string>();
        if (environment.IsDevelopment())
            return problems;

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("ChefOs")))
            problems.Add("ConnectionStrings:ChefOs est vide : indiquez la chaîne de connexion PostgreSQL.");

        CheckPayments(configuration, problems);

        var baseUrl = configuration["Vitrine:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            problems.Add("Vitrine:BaseUrl est absent : indiquez l'adresse publique de la vitrine, en https (par exemple https://reservations.mon-restaurant.be).");
        else if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            problems.Add("Vitrine:BaseUrl n'est pas une adresse absolue : indiquez l'adresse publique de la vitrine, en https.");
        else if (uri.Scheme != Uri.UriSchemeHttps)
            problems.Add("Vitrine:BaseUrl doit commencer par https:// : les liens de paiement et d'annulation envoyés aux clients ne doivent pas passer en clair.");

        CheckEmail(configuration, environment, problems);
        return problems;
    }

    /// <summary>Lève <see cref="InvalidOperationException"/> avec tous les problèmes, un par ligne ; ne fait rien s'il n'y en a pas.</summary>
    public static void EnsureValid(IConfiguration configuration, IHostEnvironment environment)
    {
        var problems = Problems(configuration, environment);
        if (problems.Count == 0)
            return;
        throw new InvalidOperationException(MessagePrefix + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "- " + p)));
    }

    private static void CheckPayments(IConfiguration configuration, List<string> problems)
    {
        var gateway = configuration["Payments:Gateway"]?.Trim();
        if (string.IsNullOrEmpty(gateway))
        {
            problems.Add("Payments:Gateway est absent : choisissez Mollie (paiements réels) ou Demo (démonstration, aucun encaissement réel).");
        }
        else if (string.Equals(gateway, PaymentOptions.MollieGateway, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuration["Mollie:ApiKey"]) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MOLLIE_API_KEY")))
                problems.Add("Payments:Gateway = Mollie sans clé : renseignez Mollie:ApiKey ou la variable d'environnement MOLLIE_API_KEY.");
        }
        else if (!string.Equals(gateway, "Demo", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Payments:Gateway doit valoir Demo ou Mollie.");
        }
    }

    private static void CheckEmail(IConfiguration configuration, IHostEnvironment environment, List<string> problems)
    {
        var sender = configuration["Email:Sender"]?.Trim();
        if (string.IsNullOrEmpty(sender))
        {
            problems.Add("Email:Sender est absent : choisissez Smtp (envoi réel) ou Log (les e-mails sont seulement journalisés).");
        }
        else if (string.Equals(sender, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuration["Email:Smtp:Host"]))
                problems.Add("Email:Sender = Smtp sans serveur : renseignez Email:Smtp:Host.");
            var from = configuration["Email:FromAddress"];
            if (string.IsNullOrWhiteSpace(from))
                problems.Add("Email:Sender = Smtp sans adresse d'expédition : renseignez Email:FromAddress.");
            else if (!MailboxAddress.TryParse(from.Trim(), out _))
                problems.Add("Email:FromAddress n'est pas une adresse e-mail valide : indiquez par exemple no-reply@mon-restaurant.be.");
            if (string.Equals(configuration["Email:Smtp:Security"]?.Trim(), "None", StringComparison.OrdinalIgnoreCase))
                problems.Add("Email:Smtp:Security = None est réservé au développement local : utilisez StartTls ou SslOnConnect.");
        }
        else if (string.Equals(sender, "Log", StringComparison.OrdinalIgnoreCase))
        {
            // Avec « Log », le lien de confirmation n'est écrit nulle part (le corps des e-mails contient des données personnelles) :
            // le premier compte ne pourrait jamais être confirmé. Seule la démo hébergée (compte déjà confirmé) ou l'absence de confirmation s'en passe.
            var confirmationRequired = configuration.GetValue("Accounts:RequireConfirmedEmail", true);
            var demoSeed = configuration.GetValue("Demo:SeedOnStartup", false);
            if (confirmationRequired && !demoSeed)
                problems.Add("Email:Sender = Log empêche de confirmer un compte (le lien de confirmation n'est jamais envoyé) : choisissez Smtp avec un vrai serveur d'e-mails, "
                    + "ou limitez Log à une démo (Demo:SeedOnStartup = true, compte de démonstration déjà confirmé).");
        }
        else
        {
            problems.Add("Email:Sender doit valoir Smtp ou Log.");
        }
    }
}
