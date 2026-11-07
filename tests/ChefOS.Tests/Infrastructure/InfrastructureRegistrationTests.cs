using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}
