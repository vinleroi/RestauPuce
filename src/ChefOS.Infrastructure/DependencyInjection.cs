using Anthropic;
using ChefOS.Core.Receipts;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Notifications;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Receipts;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Services;
using ChefOS.Infrastructure.Wines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<WineLibrarySeeder>();

        services.AddScoped<RestaurantService>();
        services.AddScoped<IngredientService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<RecipeService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<SalesService>();
        services.AddScoped<OrderService>();
        services.AddScoped<WasteService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<StatsService>();
        services.AddScoped<VitrineService>();

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
    /// Enregistre <see cref="PaymentService"/>, <see cref="ReservationService"/>, l'envoi d'e-mails (<see cref="LoggingEmailSender"/>, qui journalise) et la passerelle
    /// choisie par la section <c>Payments</c> (voir <see cref="PaymentOptions"/>) : <see cref="DemoPaymentGateway"/> par défaut (en mémoire, singleton, aussi disponible
    /// sous son type pour la page de démonstration) ; avec <c>Gateway = Mollie</c>, <see cref="MolliePaymentGateway"/> (HttpClient typé, clé <c>Mollie:ApiKey</c> ou, à défaut,
    /// <c>MOLLIE_API_KEY</c>), ou une passerelle qui échoue avec un message explicite sans clé. Choix lu à l'enregistrement : redémarrage pour le changer.
    /// Suppose <see cref="AddChefOsInfrastructure"/> déjà appelé (fabrique de contextes, <see cref="OrderService"/>, <see cref="TimeProvider"/>).
    /// </summary>
    public static IServiceCollection AddChefOsPayments(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(PaymentOptions.SectionName);
        services.Configure<PaymentOptions>(section);
        services.AddLogging();
        services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ReservationService>();

        var options = section.Get<PaymentOptions>() ?? new PaymentOptions();
        if (!string.Equals(options.Gateway?.Trim(), PaymentOptions.MollieGateway, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(provider => new DemoPaymentGateway(provider.GetRequiredService<IOptions<PaymentOptions>>().Value.BaseUrl));
            services.AddSingleton<IPaymentGateway>(provider => provider.GetRequiredService<DemoPaymentGateway>());
            return services;
        }

        var apiKey = configuration["Mollie:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("MOLLIE_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<IPaymentGateway, MissingKeyPaymentGateway>();
            return services;
        }

        services.AddSingleton(new MollieSettings(apiKey.Trim()));
        services.AddHttpClient<MolliePaymentGateway>(client =>
        {
            client.BaseAddress = new Uri(MolliePaymentGateway.BaseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddTransient<IPaymentGateway>(provider => provider.GetRequiredService<MolliePaymentGateway>());

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

        AddAnthropicClient(services, apiKey.Trim());
        services.AddSingleton<IReceiptReader, ClaudeReceiptReader>();

        return services;
    }

    /// <summary>
    /// Enregistre <see cref="WineService"/> et le <see cref="IWineDescriber"/> choisi par la section <c>Wines</c> (voir <see cref="WineDescriberOptions"/>) :
    /// <see cref="DemoWineDescriber"/> par défaut ; avec <c>Describer = Claude</c>, <see cref="ClaudeWineDescriber"/> (clé <c>Anthropic:ApiKey</c> ou, à défaut,
    /// <c>ANTHROPIC_API_KEY</c>), ou un describer qui échoue avec un message explicite quand il n'y a pas de clé. Le client Anthropic est partagé avec le lecteur
    /// de tickets (un seul enregistrement) ; le describer en prend une copie au délai de 2 minutes. Choix lu à l'enregistrement : redémarrage pour le changer.
    /// </summary>
    public static IServiceCollection AddChefOsWineDescriber(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(WineDescriberOptions.SectionName);
        services.Configure<WineDescriberOptions>(section);
        services.AddScoped<WineService>();

        var options = section.Get<WineDescriberOptions>() ?? new WineDescriberOptions();
        if (!string.Equals(options.Describer?.Trim(), WineDescriberOptions.ClaudeDescriber, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IWineDescriber, DemoWineDescriber>();
            return services;
        }

        var apiKey = configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<IWineDescriber, MissingKeyWineDescriber>();
            return services;
        }

        AddAnthropicClient(services, apiKey.Trim());
        services.AddSingleton<IWineDescriber>(provider =>
            new ClaudeWineDescriber(ClaudeWineDescriber.Configure(provider.GetRequiredService<AnthropicClient>())));

        return services;
    }

    /// <summary>
    /// Le client Anthropic partagé par les lecteurs et describers IA. Enregistré par fabrique (le conteneur le libère à l'arrêt), une seule fois : le premier
    /// enregistrement gagne. Un seul nouvel essai (2 par défaut) : le SDK relance aussi après un délai dépassé, et chaque essai est facturé. Délai explicite de
    /// 5 minutes par essai (sans lui, celui du SDK se calcule sur la taille de la réponse permise : 480 s pour 16 000 jetons) ; chaque consommateur traduit
    /// le dépassement en message français.
    /// </summary>
    private static void AddAnthropicClient(IServiceCollection services, string key) =>
        services.TryAddSingleton(_ => new AnthropicClient { ApiKey = key, MaxRetries = 1, Timeout = TimeSpan.FromMinutes(5) });
}
