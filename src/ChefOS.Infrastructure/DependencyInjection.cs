using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Enregistre le contexte PostgreSQL (fabrique scoped, donc contexte scoped aussi, nécessaire à Identity) et les services métier.
    /// <see cref="ITenantContext"/> n'est pas enregistré ici : chaque hôte fournit le sien (restaurant lu dans les claims, fixe, etc.).
    /// Le <see cref="DemoSeeder"/> s'ajoute à part, avec <see cref="AddChefOsDemoSeeder"/> : il exige Identity, que la vitrine n'a pas.
    /// </summary>
    public static IServiceCollection AddChefOsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<ChefOsDbContext>(options => options.UseNpgsql(connectionString), ServiceLifetime.Scoped);

        services.AddScoped<RestaurantService>();
        services.AddScoped<IngredientService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<RecipeService>();
        services.AddScoped<DashboardService>();

        return services;
    }

    /// <summary>
    /// Enregistre le <see cref="DemoSeeder"/> et sa configuration (section <c>Demo</c>). Le seeder crée le compte de démonstration
    /// avec <c>UserManager&lt;ApplicationUser&gt;</c> : l'hôte doit donc avoir enregistré Identity (le back ; pas la vitrine,
    /// dont la validation des services échouerait au démarrage en développement).
    /// </summary>
    public static IServiceCollection AddChefOsDemoSeeder(this IServiceCollection services)
    {
        services.AddOptions<DemoOptions>().BindConfiguration("Demo");
        services.AddScoped<DemoSeeder>();

        return services;
    }
}
