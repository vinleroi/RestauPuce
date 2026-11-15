using System.Net;
using ChefOS.Back;
using System.Text.RegularExpressions;
using ChefOS.Core.Receipts;
using ChefOS.Core.Restaurants;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Back;

/// <summary>
/// Le back (<c>WebApplicationFactory&lt;BackProgram&gt;</c>) contre une vraie base PostgreSQL de test : aucun appel externe
/// (e-mails dans le journal, passerelle Demo, lecteur de tickets et assistant de vins de démonstration).
/// Deux restaurants (Bistro, Brasserie), chacun avec un propriétaire et un ticket ; les restaurants existent avant le démarrage,
/// donc le <c>DemoSeeder</c> du mode Development ne fait rien. Un utilisateur est membre des deux restaurants.
/// </summary>
public sealed class BackSite : IAsyncDisposable
{
    public const string Password = "Chef-Test-2026!";

    public required WebApplicationFactory<BackProgram> Factory { get; init; }
    public required DbContextOptions<ChefOsDbContext> Options { get; init; }
    public required Restaurant Bistro { get; init; }
    public required Restaurant Brasserie { get; init; }
    public required ApplicationUser BistroOwner { get; init; }
    public required ApplicationUser BrasserieOwner { get; init; }
    /// <summary>Propriétaire du Bistro, aussi membre (rôle Staff) de la Brasserie.</summary>
    public required ApplicationUser Shared { get; init; }
    public int BistroReceiptId { get; init; }
    public int BrasserieReceiptId { get; init; }

    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <param name="environment">« Development » (inscription ouverte, migrations) ou « Production » (inscription fermée dès qu'un compte existe).</param>
    public static async Task<BackSite> CreateAsync(PostgresFixture postgres, string environment = "Development")
    {
        var options = await postgres.CreateDatabaseAsync();
        var connection = new ChefOsDbContext(options, new FixedTenantContext(null)).Database.GetConnectionString()!;

        var bistro = new Restaurant { Name = "Bistro Test", Slug = "bistro-test", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var brasserie = new Restaurant { Name = "Brasserie Test", Slug = "brasserie-test", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
        {
            db.Restaurants.AddRange(bistro, brasserie);
            await db.SaveChangesAsync();
        }

        var factory = CreateFactory(connection, environment);

        ApplicationUser bistroOwner, brasserieOwner, shared;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            bistroOwner = await AddUserAsync(users, "patron.bistro@chefos.test");
            brasserieOwner = await AddUserAsync(users, "patron.brasserie@chefos.test");
            shared = await AddUserAsync(users, "partage@chefos.test");
        }

        int bistroReceipt, brasserieReceipt;
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
        {
            db.Memberships.AddRange(
                new Membership { UserId = bistroOwner.Id, RestaurantId = bistro.Id, Role = MembershipRole.Owner },
                new Membership { UserId = brasserieOwner.Id, RestaurantId = brasserie.Id, Role = MembershipRole.Owner },
                new Membership { UserId = shared.Id, RestaurantId = bistro.Id, Role = MembershipRole.Owner },
                new Membership { UserId = shared.Id, RestaurantId = brasserie.Id, Role = MembershipRole.Staff });
            await db.SaveChangesAsync();
        }
        bistroReceipt = await AddReceiptAsync(options, bistro.Id, "Grossiste du Bistro");
        brasserieReceipt = await AddReceiptAsync(options, brasserie.Id, "Grossiste de la Brasserie");

        return new BackSite
        {
            Factory = factory, Options = options, Bistro = bistro, Brasserie = brasserie,
            BistroOwner = bistroOwner, BrasserieOwner = brasserieOwner, Shared = shared,
            BistroReceiptId = bistroReceipt, BrasserieReceiptId = brasserieReceipt
        };
    }

    /// <summary>
    /// L'hôte du back sur une base donnée, configuré comme en production (adresse https de la vitrine, e-mails journalisés, passerelle Demo,
    /// pas de seed) ; <paramref name="settings"/> complète ou remplace ces réglages. Aucun appel externe.
    /// </summary>
    public static WebApplicationFactory<BackProgram> CreateFactory(string connection, string environment = "Development", params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<BackProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:ChefOs", connection);
            builder.UseSetting("Email:Sender", "Log");
            builder.UseSetting("Payments:Gateway", "Demo");
            // Adresse https : la configuration de Production est vérifiée au démarrage (ProductionConfiguration).
            builder.UseSetting("Vitrine:BaseUrl", "https://vitrine.test");
            builder.UseSetting("Accounts:RequireConfirmedEmail", "false");
            builder.UseSetting("Demo:SeedOnStartup", "false");
            // Jamais d'appel à Anthropic : lecteur de tickets et assistant de vins de démonstration.
            builder.UseSetting("Receipts:Reader", "Demo");
            builder.UseSetting("Wines:Describer", "Demo");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });

    private static async Task<ApplicationUser> AddUserAsync(UserManager<ApplicationUser> users, string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<int> AddReceiptAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, string supplier)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId));
        var receipt = new Receipt
        {
            SupplierNameRaw = supplier, Status = ReceiptStatus.Extracted, CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        db.ReceiptFiles.Add(new ReceiptFile { ReceiptId = receipt.Id, FileName = "ticket.png", ContentType = "image/png", Data = [137, 80, 78, 71] });
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    /// <summary>Poste le formulaire de connexion (jeton anti-falsification inclus) ; le cookie de session reste dans le client.</summary>
    public static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsync("/Account/Login", await FormAsync(client, "/Account/Login", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>Un formulaire prêt à poster : les champs cachés (jeton, <c>_handler</c>) lus sur la page, complétés par <paramref name="fields"/>.</summary>
    public static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string pageUrl, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(pageUrl);
        var all = HiddenFields(html);
        foreach (var (name, value) in fields)
            all[name] = value;
        return new FormUrlEncodedContent(all);
    }

    /// <summary>Les champs <c>&lt;input type="hidden"&gt;</c> d'une page, par nom (le premier de chaque nom).</summary>
    public static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(html, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            var name = Attribute(input.Value, "name");
            if (name is not null && !fields.ContainsKey(name))
                fields[name] = WebUtility.HtmlDecode(Attribute(input.Value, "value") ?? "");
        }
        return fields;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{name}=\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}
