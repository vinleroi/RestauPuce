using System.Globalization;
using ChefOS.Back.Components;
using ChefOS.Back.Components.Account;
using ChefOS.Back.Receipts;
using ChefOS.Back.Tenancy;
using ChefOS.Back.Vitrine;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
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

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ChefOsDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<FrenchIdentityErrorDescriber>()
    .AddClaimsPrincipalFactory<ChefOsClaimsPrincipalFactory>();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();

    // Base de dev à jour et « Le Petit Bistro » au premier lancement (le seeder ne fait rien si un restaurant existe).
    // Hors requête, ClaimsTenantContext ne voit aucun restaurant : seuls Identity, Restaurant et Membership sont touchés ici.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ChefOsDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
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
