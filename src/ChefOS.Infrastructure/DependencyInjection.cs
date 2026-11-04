using Anthropic;
using ChefOS.Core.Receipts;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Receipts;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        services.AddScoped<SalesService>();
        services.AddScoped<OrderService>();
        services.AddScoped<WasteService>();
        services.AddScoped<InventoryService>();

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

    /// <summary>
    /// Enregistre <see cref="ReceiptService"/> et le <see cref="IReceiptReader"/> choisi par la section <c>Receipts</c> (voir <see cref="ReceiptReaderOptions"/>) :
    /// <see cref="DemoReceiptReader"/> avec <c>Reader = Demo</c> ; sinon <see cref="ClaudeReceiptReader"/>, avec la clé <c>Anthropic:ApiKey</c> ou, à défaut,
    /// la variable d'environnement <c>ANTHROPIC_API_KEY</c>. Sans clé, le back démarre quand même : le lecteur enregistré fait échouer chaque lecture
    /// avec un message explicite (le ticket passe en « échec » et le chef le saisit à la main).
    /// Les lecteurs sont des singletons : ils ne gardent aucun état, et le client Anthropic (qui porte son <c>HttpClient</c>) est fait pour être partagé.
    /// Choix lu à l'enregistrement : changer de lecteur ou de clé demande un redémarrage.
    /// </summary>
    public static IServiceCollection AddChefOsReceiptReader(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ReceiptReaderOptions.SectionName);
        services.Configure<ReceiptReaderOptions>(section);
        services.AddScoped<ReceiptService>();

        var options = section.Get<ReceiptReaderOptions>() ?? new ReceiptReaderOptions();
        if (string.Equals(options.Reader?.Trim(), ReceiptReaderOptions.DemoReader, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IReceiptReader, DemoReceiptReader>();
            return services;
        }

        var apiKey = configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<IReceiptReader, MissingKeyReceiptReader>();
            return services;
        }

        var key = apiKey.Trim();
        // Enregistré par fabrique : le conteneur possède le client et le libère à l'arrêt.
        // Un seul nouvel essai (2 par défaut) : le SDK relance aussi après un délai dépassé, et chaque essai est facturé. Délai explicite de 5 minutes
        // par essai (sans lui, celui du SDK se calcule sur la taille de la réponse permise : 480 s pour 16 000 jetons) ; ClaudeReceiptReader traduit
        // le dépassement en message français.
        services.AddSingleton(_ => new AnthropicClient { ApiKey = key, MaxRetries = 1, Timeout = TimeSpan.FromMinutes(5) });
        services.AddSingleton<IReceiptReader, ClaudeReceiptReader>();

        return services;
    }
}
