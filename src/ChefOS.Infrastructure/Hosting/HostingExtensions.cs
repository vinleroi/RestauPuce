using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using GlobalContextFactory = ChefOS.Infrastructure.DependencyInjection.GlobalContextFactory;
using IPNetwork = System.Net.IPNetwork;

namespace ChefOS.Infrastructure.Hosting;

/// <summary>
/// Hébergement commun des deux sites, derrière un proxy (Caddy en production) : en-têtes transférés acceptés des seuls réseaux déclarés,
/// clés de protection des données en base (un redéploiement ne déconnecte personne), contrôles de santé sans aucune donnée.
/// </summary>
public static class HostingExtensions
{
    /// <summary>Réseaux du proxy, en notation CIDR : liste (<c>ReverseProxy:KnownNetworks:0</c>…) ou valeurs séparées par des virgules.</summary>
    public const string KnownNetworksKey = "ReverseProxy:KnownNetworks";
    /// <summary>Le processus répond.</summary>
    public const string LivenessPath = "/healthz";
    /// <summary>La base est joignable et aucune migration n'attend.</summary>
    public const string ReadinessPath = "/healthz/ready";

    private const string ReadyTag = "ready";
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// En-têtes <c>X-Forwarded-For</c>, <c>-Proto</c> et <c>-Host</c> acceptés des seuls réseaux de <see cref="KnownNetworksKey"/> (aucun par défaut) ;
    /// clés de protection des données dans la table <c>DataProtectionKeys</c>, sous le nom d'application <paramref name="applicationName"/> ;
    /// contrôle de santé « base » (connexion et migrations). Un réseau mal écrit empêche le démarrage.
    /// Suppose <see cref="DependencyInjection.AddChefOsInfrastructure"/> déjà appelé (fabrique de contextes sans restaurant).
    /// </summary>
    public static IServiceCollection AddChefOsHosting(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        var networks = KnownNetworks(configuration);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Par défaut, ASP.NET Core accepte la boucle locale : ici, seuls les réseaux déclarés comptent.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
            // Listes vides = le middleware accepterait les en-têtes de n'importe quelle adresse : sans réseau déclaré, il ne fait donc rien.
            options.ForwardedHeaders = networks.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        });

        services.AddDataProtection().SetApplicationName(applicationName);
        services.AddOptions<KeyManagementOptions>()
            .Configure<GlobalContextFactory>((options, contexts) => options.XmlRepository = new DatabaseKeyRepository(contexts));

        services.AddHealthChecks().Add(new HealthCheckRegistration("base",
            provider => new DatabaseReadyCheck(provider.GetRequiredService<GlobalContextFactory>()),
            HealthStatus.Unhealthy, [ReadyTag], ReadinessTimeout));

        return services;
    }

    /// <summary>
    /// À appeler juste après <c>Build()</c> : <c>UseForwardedHeaders</c> passe avant HSTS, la redirection HTTPS et l'authentification
    /// (schéma https vu derrière le proxy : pas de boucle de redirection, cookies <c>Secure</c>). Puis <see cref="LivenessPath"/> et <see cref="ReadinessPath"/>.
    /// </summary>
    public static WebApplication UseChefOsHosting(this WebApplication app)
    {
        app.UseForwardedHeaders();

        app.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteStatusAsync })
            .AllowAnonymous();
        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag), ResponseWriter = WriteStatusAsync })
            .AllowAnonymous();

        return app;
    }

    /// <summary>Les réseaux de <see cref="KnownNetworksKey"/> ; une entrée qui n'est pas un réseau CIDR lève une exception au message en français.</summary>
    internal static IReadOnlyList<IPNetwork> KnownNetworks(IConfiguration configuration)
    {
        var section = configuration.GetSection(KnownNetworksKey);
        var entries = section.GetChildren().Select(child => child.Value).Append(section.Value)
            .SelectMany(value => (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var networks = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (!IPNetwork.TryParse(entry, out var network))
                throw new InvalidOperationException(
                    $"{KnownNetworksKey} : « {entry} » n'est pas un réseau CIDR valide. Indiquez l'adresse du réseau et sa longueur, par exemple 172.30.0.0/16.");
            networks.Add(network);
        }

        return networks;
    }

    // Texte brut « Healthy », « Unhealthy » ou « Degraded » : ni nom de contrôle, ni durée, ni message d'erreur.
    private static Task WriteStatusAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(report.Status.ToString());
    }

    /// <summary>
    /// Prête : la base répond et aucune migration n'attend (la vitrine n'est prête qu'une fois le schéma installé par le back).
    /// Contexte sans restaurant : aucune table d'un restaurant n'est lue.
    /// </summary>
    private sealed class DatabaseReadyCheck(GlobalContextFactory contexts) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await using var db = contexts.CreateDbContext();
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("Base injoignable.");

            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            return pending.Any()
                ? HealthCheckResult.Unhealthy("Migrations en attente : le back ne les a pas encore appliquées.")
                : HealthCheckResult.Healthy();
        }
    }

    /// <summary>
    /// Les clés dans <c>DataProtectionKeys</c>, communes aux deux sites ; chacun isole ses données par son nom d'application.
    /// Contextes de la fabrique sans restaurant, comme la file d'e-mails : <c>PersistKeysToDbContext</c> prendrait le contexte du conteneur,
    /// donc dans le back celui du restaurant de la requête en cours. Le XML des clés n'est jamais journalisé.
    /// </summary>
    private sealed class DatabaseKeyRepository(GlobalContextFactory contexts) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            using var db = contexts.CreateDbContext();
            return db.DataProtectionKeys.AsNoTracking().Select(key => key.Xml).ToList()
                .Where(xml => !string.IsNullOrEmpty(xml))
                .Select(xml => XElement.Parse(xml!))
                .ToList();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            using var db = contexts.CreateDbContext();
            db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting) });
            db.SaveChanges();
        }
    }
}
