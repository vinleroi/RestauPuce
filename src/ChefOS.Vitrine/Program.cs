using System.Globalization;
using System.Threading.RateLimiting;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Wines;
using ChefOS.Vitrine.Components;
using ChefOS.Vitrine.Endpoints;
using ChefOS.Vitrine.Support;
using ChefOS.Vitrine.Tenancy;
using Microsoft.AspNetCore.Localization;

// Interface en français de Belgique : montants « 12,60 € », dates « 06/11/2026 ».
var culture = new CultureInfo("fr-BE");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

// Pages statiques (carte, vins, statut) ; la réservation et l'annulation sont interactives (Interactive Server).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(culture);
    options.SupportedCultures = [culture];
    options.SupportedUICultures = [culture];
});

var connectionString = builder.Configuration.GetConnectionString("ChefOs");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("La chaîne de connexion « ChefOs » n'est pas configurée.");

builder.Services.AddChefOsInfrastructure(connectionString);
// Réservations et paiements : passerelle choisie par la section « Payments » (Demo par défaut, en base, partagée avec le back).
builder.Services.AddChefOsPayments(builder.Configuration);
// Carte des vins publique (lecture seule ici) : l'assistant de rédaction n'est utilisé que par le back, Demo par défaut.
builder.Services.AddChefOsWineDescriber(builder.Configuration);

// Restaurant courant : celui du lien /{slug}, jamais un utilisateur connecté. Un contexte par requête et par circuit interactif.
builder.Services.AddScoped<SlugTenantContext>();
builder.Services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<SlugTenantContext>());
builder.Services.AddScoped<SlugResolver>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<PublicUrls>();
builder.Services.AddSingleton<AttemptLimiter>();
builder.Services.AddScoped<CircuitAttempts>();

// 10 tentatives par minute et par adresse IP sur le webhook (HTTP) ; la création et l'annulation, qui passent par le circuit interactif,
// sont gardées par CircuitAttempts avec la même limite.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(VitrineEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "inconnue", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = AttemptLimiter.PermitLimit, Window = AttemptLimiter.Window, QueueLimit = 0, AutoReplenishment = true
        }));
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = "60";
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(AttemptLimiter.Message, token);
    };
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers.XFrameOptions = "DENY";
    await next();
});

app.UseRequestLocalization();

// Routage explicite : le slug du lien doit être connu avant d'atteindre l'endpoint (restaurant courant, 404 si inconnu ou non publié).
app.UseRouting();
app.UseMiddleware<SlugTenantMiddleware>();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapVitrineEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Bibliothèque de vins partagée : upsert idempotent à chaque démarrage, quel que soit l'environnement.
// Une base pas encore migrée ne doit pas empêcher l'hôte de démarrer : l'échec est journalisé.
{
    await using var wineScope = app.Services.CreateAsyncScope();
    try
    {
        await wineScope.ServiceProvider.GetRequiredService<WineLibrarySeeder>().SeedAsync();
    }
    catch (Exception ex)
    {
        wineScope.ServiceProvider.GetRequiredService<ILogger<WineLibrarySeeder>>()
            .LogError(ex, "Bibliothèque de vins non chargée (base absente ou non migrée ?).");
    }
}

app.Run();

/// <summary>Repère du site public pour les tests d'intégration (<c>WebApplicationFactory&lt;VitrineProgram&gt;</c>) : le back a son propre <c>Program</c>.</summary>
public partial class VitrineProgram;
