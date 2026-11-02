using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Enregistre le contexte PostgreSQL (fabrique scoped, donc contexte scoped aussi, nécessaire à Identity), les services métier
    /// et le <see cref="DemoSeeder"/> (section de configuration <c>Demo</c>).
    /// <see cref="ITenantContext"/> n'est pas enregistré ici : chaque hôte fournit le sien (restaurant lu dans les claims, fixe, etc.).
    /// </summary>
    public static IServiceCollection AddChefOsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<ChefOsDbContext>(options => options.UseNpgsql(connectionString), ServiceLifetime.Scoped);

        services.AddOptions<DemoOptions>().BindConfiguration("Demo");

        services.AddScoped<RestaurantService>();
        services.AddScoped<IngredientService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<RecipeService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<DemoSeeder>();

        return services;
    }
}
