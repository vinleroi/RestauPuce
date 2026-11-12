using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Notifications;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Enregistrement des services sans base : seule la validation du conteneur est exercée, aucune connexion n'est ouverte.</summary>
public class InfrastructureRegistrationTests
{
    private const string ConnectionString = "Host=localhost;Port=1;Database=chefos_unused;Username=u;Password=p";

    [Fact]
    public void AddChefOsInfrastructure_ValidatesWithoutIdentity_AsTheVitrineHostsIt()
    {
        // La vitrine n'a ni Identity ni UserManager. Le conteneur du mode Development valide tous les services au démarrage :
        // un service d'infrastructure qui exigerait Identity ferait échouer le démarrage de la vitrine.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(ConnectionString);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RestaurantService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DashboardService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WasteService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<InventoryService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StatsService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VitrineService>());
        Assert.Null(scope.ServiceProvider.GetService<DemoSeeder>());
    }

    [Fact]
    public void AddChefOsDemoSeeder_ValidatesOnceIdentityIsRegistered_AsTheBackHostsIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());   // la section « Demo » se lie sur la configuration de l'hôte
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(ConnectionString);
        services.AddChefOsDemoSeeder();
        services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ChefOsDbContext>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DemoSeeder>());
    }

    [Theory]
    [InlineData(null, typeof(LoggingEmailTransport))]
    [InlineData("Log", typeof(LoggingEmailTransport))]
    [InlineData("smtp", typeof(SmtpEmailTransport))]
    public void AddChefOsEmail_ChoosesTheTransport_AndRegistersTheDispatcherInBothSites(string? sender, Type expected)
    {
        // Le dispatcher est un singleton : il ne peut pas dépendre de la fabrique de contextes scoped (restaurant courant) ; la validation le vérifie.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(ConnectionString);
        var settings = new Dictionary<string, string?> { ["Email:Sender"] = sender, ["Email:Smtp:Security"] = "SslOnConnect" };
        services.AddChefOsEmail(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType(expected, provider.GetRequiredService<IEmailTransport>());
        Assert.Same(provider.GetRequiredService<EmailDispatchSignal>(), provider.GetRequiredService<EmailDispatchSignal>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<OutgoingEmailDispatcher>());
        var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
        Assert.Equal(("ChefOS", 587, SmtpSecurity.SslOnConnect), (options.FromName, options.Smtp.Port, options.Smtp.Security));
    }

    [Fact]
    public void EmailOptions_Defaults_AreLogAndStartTls()
    {
        var options = new EmailOptions();

        Assert.Equal((EmailOptions.LogSender, "ChefOS", 587, SmtpSecurity.StartTls), (options.Sender, options.FromName, options.Smtp.Port, options.Smtp.Security));
    }
}
