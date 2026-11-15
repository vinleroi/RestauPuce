using System.Net;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Hosting;
using ChefOS.Tests.Back;
using ChefOS.Tests.Vitrine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Hébergement derrière un proxy et démarrage : en-têtes transférés acceptés des seuls réseaux déclarés, contrôles de santé sans détail,
/// clés de protection des données en base, migrations et bibliothèque de vins par le back seul.
/// </summary>
[Collection("postgres")]
public class HostingTests(PostgresFixture postgres)
{
    // --- En-têtes transférés (sonde : un hôte minimal avec AddChefOsHosting / UseChefOsHosting et un endpoint qui renvoie ce qu'il voit) ---

    [Fact]
    public async Task ForwardedProto_FromAKnownNetwork_IsHonoured()
    {
        await using var app = await StartProbeAsync(("ReverseProxy:KnownNetworks", "10.0.0.0/8"));

        var seen = await ProbeAsync(app, from: "10.1.2.3");

        Assert.Equal("https|203.0.113.7|app.chefos.test", seen);
    }

    [Fact]
    public async Task ForwardedProto_FromAnUnknownAddress_IsIgnored()
    {
        await using var app = await StartProbeAsync(("ReverseProxy:KnownNetworks", "10.0.0.0/8"));

        var seen = await ProbeAsync(app, from: "192.168.1.20");

        Assert.Equal("http|192.168.1.20|localhost", seen);
    }

    [Fact]
    public async Task ForwardedProto_WithoutKnownNetworks_IsIgnored()
    {
        // Sans réseau déclaré, le middleware d'ASP.NET Core ferait confiance à toutes les adresses : rien ne doit alors être accepté.
        await using var app = await StartProbeAsync();

        var seen = await ProbeAsync(app, from: "10.1.2.3");

        Assert.Equal("http|10.1.2.3|localhost", seen);
    }

    [Fact]
    public async Task ForwardedProto_FromAnyListedNetwork_IsHonoured()
    {
        // Forme des variables d'environnement (ReverseProxy__KnownNetworks__0…) ; Kestrel peut présenter l'adresse IPv4 du proxy en IPv6 mappée.
        await using var app = await StartProbeAsync(("ReverseProxy:KnownNetworks:0", "172.30.0.0/16"), ("ReverseProxy:KnownNetworks:1", "10.0.0.0/8"));

        Assert.StartsWith("https|", await ProbeAsync(app, from: "172.30.0.5"));
        Assert.StartsWith("https|", await ProbeAsync(app, from: "::ffff:172.30.0.5"));
        Assert.StartsWith("https|", await ProbeAsync(app, from: "10.1.2.3"));
        Assert.StartsWith("http|", await ProbeAsync(app, from: "172.31.0.5"));
    }

    [Fact]
    public async Task KnownNetworks_Invalid_RefusesToStart()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartProbeAsync(("ReverseProxy:KnownNetworks", "10.0.0.0/33")));

        Assert.Contains("ReverseProxy:KnownNetworks", error.Message);
    }

    // --- Contrôles de santé ---

    [Fact]
    public async Task Ready_ReturnsServiceUnavailable_WhenMigrationsArePending()
    {
        var options = await postgres.CreateEmptyDatabaseAsync();
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
        {
            // La base répond : c'est bien le schéma absent (migrations en attente) qui rend la vitrine « pas prête ».
            Assert.True(await db.Database.CanConnectAsync());
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
        }
        await using var vitrine = VitrineSite.CreateFactory(PostgresFixture.ConnectionStringOf(options));
        using var client = vitrine.CreateClient();

        var live = await client.GetAsync("/healthz");
        var ready = await client.GetAsync("/healthz/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_ReturnsOk_OnAMigratedDatabase()
    {
        await using var vitrine = await VitrineSite.CreateAsync(postgres);
        await using var back = await BackSite.CreateAsync(postgres);

        foreach (var client in new[] { vitrine.Client, back.CreateClient() })
        {
            using (client)
            {
                var live = await client.GetAsync("/healthz");
                var ready = await client.GetAsync("/healthz/ready");

                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                // Texte brut, sans détail : ni nom de contrôle, ni durée, ni message d'erreur.
                Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
            }
        }
    }

    // --- Clés de protection des données ---

    [Fact]
    public async Task DataProtectionKeys_AreStoredInTheDatabase()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();

        // La page de connexion porte un jeton anti-falsification : il faut une clé pour le produire.
        var login = await client.GetStringAsync("/Account/Login");

        Assert.Contains("__RequestVerificationToken", login);
        await using var db = new ChefOsDbContext(site.Options, new FixedTenantContext(null));
        Assert.True(await db.DataProtectionKeys.CountAsync() >= 1);
    }

    // --- Démarrage : migrations, seed et bibliothèque de vins par le back seul ---

    [Fact]
    public async Task BackStartup_MigratesAnEmptyDatabase_AndLoadsTheWineLibrary()
    {
        var options = await postgres.CreateEmptyDatabaseAsync();
        await using var back = BackSite.CreateFactory(PostgresFixture.ConnectionStringOf(options), "Production", ("Database:MigrateOnStartup", "true"));
        using var client = back.CreateClient();

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.WineReferences.CountAsync() > 0);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz/ready")).StatusCode);
    }

    [Fact]
    public async Task BackStartup_WithMigrateOnStartupFalse_LeavesTheDatabaseUnmigrated()
    {
        var options = await postgres.CreateEmptyDatabaseAsync();
        await using var back = BackSite.CreateFactory(PostgresFixture.ConnectionStringOf(options), "Production", ("Database:MigrateOnStartup", "false"));
        using var client = back.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/healthz/ready")).StatusCode);
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task BackStartup_OutsideDevelopment_SeedsTheDemo_WhenSeedOnStartupIsTrue()
    {
        var options = await postgres.CreateEmptyDatabaseAsync();
        // Démo hébergée : le compte vient de la configuration de l'hôte (variables d'environnement), jamais d'un fichier versionné.
        await using var back = BackSite.CreateFactory(PostgresFixture.ConnectionStringOf(options), "Production",
            ("Demo:SeedOnStartup", "true"), ("Demo:Email", "demo@chefos.test"), ("Demo:Password", "Demo-Hebergee-2026!"));
        using var client = back.CreateClient();

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        Assert.True(await db.Restaurants.AnyAsync(r => r.Slug == "le-petit-bistro"));
        Assert.True(await db.Users.AnyAsync(u => u.Email == "demo@chefos.test"));
    }

    [Fact]
    public async Task Vitrine_DoesNotSeedTheWineLibrary()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        using var client = site.Client;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/{site.Bistro.Slug}")).StatusCode);

        await using var db = new ChefOsDbContext(site.Options, new FixedTenantContext(null));
        Assert.Equal(0, await db.WineReferences.CountAsync());
    }

    /// <summary>Hôte minimal (Production, serveur de test) : la base n'est jamais ouverte, la sonde n'interroge ni la santé ni les clés.</summary>
    private static async Task<WebApplication> StartProbeAsync(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value)));
        builder.Services.AddChefOsInfrastructure("Host=127.0.0.1;Port=1;Database=jamais_ouverte");
        builder.Services.AddChefOsHosting(builder.Configuration, "ChefOS.Tests");

        var app = builder.Build();
        app.UseChefOsHosting();
        app.MapGet("/probe", (HttpContext context) =>
            $"{(context.Request.IsHttps ? "https" : "http")}|{context.Connection.RemoteIpAddress}|{context.Request.Host}");
        await app.StartAsync();
        return app;
    }

    /// <summary>Une requête venue de <paramref name="from"/>, avec les trois en-têtes qu'un proxy ajoute ; renvoie « schéma|client|hôte » vus par l'endpoint.</summary>
    private static async Task<string> ProbeAsync(WebApplication app, string from)
    {
        var context = await app.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = "/probe";
            c.Connection.RemoteIpAddress = IPAddress.Parse(from);
            c.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
            c.Request.Headers["X-Forwarded-Proto"] = "https";
            c.Request.Headers["X-Forwarded-Host"] = "app.chefos.test";
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
