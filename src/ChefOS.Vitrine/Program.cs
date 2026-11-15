using System.Globalization;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Hosting;
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

// Hors développement, refuse de démarrer si la configuration est incomplète (message en français, sans valeur secrète).
ProductionConfiguration.EnsureValid(builder.Configuration, builder.Environment);

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
// Derrière le proxy : en-têtes transférés (ReverseProxy:KnownNetworks), clés de protection des données en base, /healthz et /healthz/ready.
// La vitrine ne migre pas et ne charge pas la bibliothèque de vins : le back installe le schéma, et /healthz/ready attend qu'il l'ait fait.
builder.Services.AddChefOsHosting(builder.Configuration, "ChefOS.Vitrine");
// Réservations et paiements : passerelle choisie par la section « Payments » (Demo par défaut, en base, partagée avec le back).
builder.Services.AddChefOsPayments(builder.Configuration);
// E-mails : file en base envoyée en arrière-plan (section « Email » : Log par défaut, Smtp vers Mailpit en développement).
builder.Services.AddChefOsEmail(builder.Configuration);
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

var app = builder.Build();

// En premier : le schéma et l'adresse transmis par le proxy valent pour HSTS, la redirection HTTPS et les cookies.
app.UseChefOsHosting();

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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapVitrineEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Repère du site public pour les tests d'intégration (<c>WebApplicationFactory&lt;VitrineProgram&gt;</c>) : le back a son propre <c>Program</c>.</summary>
public partial class VitrineProgram;
