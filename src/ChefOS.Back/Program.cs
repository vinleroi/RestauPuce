using System.Globalization;
using ChefOS.Back.Components;
using ChefOS.Back.Components.Account;
using ChefOS.Back.Receipts;
using ChefOS.Back.Tenancy;
using ChefOS.Back.Vitrine;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Hosting;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Seeding;
using ChefOS.Infrastructure.Wines;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// Interface en français de Belgique : montants « 12,60 € », saisie décimale avec virgule.
var culture = new CultureInfo("fr-BE");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

// Hors développement, refuse de démarrer si la configuration est incomplète (message en français, sans valeur secrète).
ProductionConfiguration.EnsureValid(builder.Configuration, builder.Environment);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(culture);
    options.SupportedCultures = [culture];
    options.SupportedUICultures = [culture];
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("ChefOs");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("La chaîne de connexion « ChefOs » n'est pas configurée.");

builder.Services.AddChefOsInfrastructure(connectionString);
// Derrière le proxy : en-têtes transférés (ReverseProxy:KnownNetworks), clés de protection des données en base, /healthz et /healthz/ready.
builder.Services.AddChefOsHosting(builder.Configuration, "ChefOS.Back");
builder.Services.AddChefOsDemoSeeder();
// Réservations et paiements : le back installe, annule et rembourse (passerelle choisie par la section « Payments », Demo par défaut).
builder.Services.AddChefOsPayments(builder.Configuration);
// E-mails : file en base envoyée en arrière-plan (section « Email » : Log par défaut, Smtp vers Mailpit en développement).
builder.Services.AddChefOsEmail(builder.Configuration);
// Lecteur de tickets : Claude (clé « Anthropic:ApiKey » ou ANTHROPIC_API_KEY) ou démonstration (« Receipts:Reader » = « Demo »).
builder.Services.AddChefOsReceiptReader(builder.Configuration);
// Assistant de rédaction des fiches de vin : démonstration par défaut, Claude avec « Wines:Describer » = « Claude » (même clé que les tickets).
builder.Services.AddChefOsWineDescriber(builder.Configuration);
builder.Services.AddHttpContextAccessor();
// Restaurant courant lu dans le claim « chefos:restaurant » : il pilote l'isolation multi-restaurant du contexte.
builder.Services.AddScoped<ITenantContext, ClaimsTenantContext>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
        // Vrai par défaut : Identity n'envoie le lien « mot de passe oublié » qu'à une adresse confirmée (faux en développement).
        options.SignIn.RequireConfirmedAccount = builder.Configuration.GetValue("Accounts:RequireConfirmedEmail", true))
    .AddEntityFrameworkStores<ChefOsDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<FrenchIdentityErrorDescriber>()
    .AddClaimsPrincipalFactory<ChefOsClaimsPrincipalFactory>();

// E-mails des comptes : dans la file commune (la fabrique de contextes est scoped).
builder.Services.AddScoped<IEmailSender<ApplicationUser>, IdentityOutboxEmailSender>();
builder.Services.AddScoped<RegistrationPolicy>();

var app = builder.Build();

// En premier : le schéma et l'adresse transmis par le proxy valent pour HSTS, la redirection HTTPS et les cookies.
app.UseChefOsHosting();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Explicites, et après les pages d'erreur : les pages /Error et /not-found portent [Authorize] + [AllowAnonymous],
// et leur réexécution doit repasser par l'autorisation (sinon : « contains authorization metadata » → 500).
app.UseAuthentication();
app.UseAuthorization();

app.UseRequestLocalization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapActiveRestaurantEndpoint();
app.MapReceiptFileEndpoint();
app.MapVitrineImageEndpoint();

// La base au démarrage, dans tous les environnements (la vitrine n'y touche pas) :
// 1. migrations si Database:MigrateOnStartup (vrai par défaut) ; le verrou de migration d'EF Core empêche deux instances de migrer ensemble,
//    et un échec arrête le démarrage ;
// 2. « Le Petit Bistro » si Demo:SeedOnStartup (vrai en développement, faux ailleurs ; une démo hébergée donne le compte par Demo__Email
//    et Demo__Password) ; le seeder ne fait rien si un restaurant existe. Hors requête, ClaimsTenantContext ne voit aucun restaurant :
//    seuls Identity, Restaurant et Membership sont touchés ;
// 3. la bibliothèque de vins partagée : upsert idempotent ; sur une base non migrée, l'échec est journalisé et l'hôte démarre quand même.
await using (var scope = app.Services.CreateAsyncScope())
{
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
        await scope.ServiceProvider.GetRequiredService<ChefOsDbContext>().Database.MigrateAsync();

    if (app.Configuration.GetValue("Demo:SeedOnStartup", false))
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();

    try
    {
        await scope.ServiceProvider.GetRequiredService<WineLibrarySeeder>().SeedAsync();
    }
    catch (Exception ex)
    {
        scope.ServiceProvider.GetRequiredService<ILogger<WineLibrarySeeder>>()
            .LogError(ex, "Bibliothèque de vins non chargée (base absente ou non migrée ?).");
    }
}

app.Run();

/// <summary>Repère du back pour les tests d'intégration (<c>WebApplicationFactory&lt;BackProgram&gt;</c>) : la vitrine a son propre <c>Program</c>.</summary>
public partial class BackProgram;
