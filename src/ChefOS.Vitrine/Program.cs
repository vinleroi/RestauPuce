using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Wines;
using ChefOS.Vitrine.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents();

var connectionString = builder.Configuration.GetConnectionString("ChefOs");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("La chaîne de connexion « ChefOs » n'est pas configurée.");

builder.Services.AddChefOsInfrastructure(connectionString);
// Site public, sans utilisateur connecté : aucun restaurant courant. Les pages liront un restaurant par son slug.
builder.Services.AddSingleton<ITenantContext>(new FixedTenantContext(null));

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();

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
