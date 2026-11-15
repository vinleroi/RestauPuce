using System.Net;
using ChefOS.Back.Components.Account;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Back;

/// <summary>Le back hébergé pour de vrai : cookie de connexion, claim du restaurant, filtre par restaurant, endpoints.</summary>
[Collection("postgres")]
public class BackSiteTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ReceiptFile_OfAnotherRestaurant_Returns404()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();
        await BackSite.LoginAsync(client, site.BistroOwner.Email!, BackSite.Password);

        var own = await client.GetAsync($"/receipts/{site.BistroReceiptId}/file");
        var foreign = await client.GetAsync($"/receipts/{site.BrasserieReceiptId}/file");
        var unknown = await client.GetAsync("/receipts/999999/file");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("image/png", own.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ReceiptFile_WithoutSession_RedirectsToLogin()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();

        var response = await client.GetAsync($"/receipts/{site.BistroReceiptId}/file");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Login_WithAWrongPassword_ShowsTheSingleGenericMessage()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();

        var wrongPassword = await client.PostAsync("/Account/Login", await BackSite.FormAsync(client, "/Account/Login", new()
        {
            ["Input.Email"] = site.BistroOwner.Email!, ["Input.Password"] = "pas-le-bon"
        }));
        var unknownUser = await client.PostAsync("/Account/Login", await BackSite.FormAsync(client, "/Account/Login", new()
        {
            ["Input.Email"] = "inconnu@chefos.test", ["Input.Password"] = BackSite.Password
        }));

        
        Assert.Equal(HttpStatusCode.OK, wrongPassword.StatusCode);
        Assert.Contains(LoginFailure.Message, WebUtility.HtmlDecode(await wrongPassword.Content.ReadAsStringAsync()));
        Assert.Contains(LoginFailure.Message, WebUtility.HtmlDecode(await unknownUser.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task ActiveRestaurant_NotAMember_Returns403()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();
        await BackSite.LoginAsync(client, site.BistroOwner.Email!, BackSite.Password);

        var response = await PostActiveRestaurantAsync(client, site.Brasserie.Id.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(await ActiveRestaurantOfAsync(site, site.BistroOwner), site.Bistro.Id);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    public async Task ActiveRestaurant_InvalidId_Returns400(string restaurantId)
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();
        await BackSite.LoginAsync(client, site.BistroOwner.Email!, BackSite.Password);

        var response = await PostActiveRestaurantAsync(client, restaurantId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ActiveRestaurant_Member_Redirects302_AndTheNextPageShowsItsData()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        using var client = site.CreateClient();
        await BackSite.LoginAsync(client, site.Shared.Email!, BackSite.Password);

        // Membre des deux restaurants : le premier par nom (Bistro) est actif à la connexion.
        var before = await client.GetStringAsync("/receipts");
        Assert.Contains("Grossiste du Bistro", before);
        Assert.DoesNotContain("Grossiste de la Brasserie", before);

        var response = await PostActiveRestaurantAsync(client, site.Brasserie.Id.ToString(), returnUrl: "/receipts");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/receipts", response.Headers.Location!.OriginalString);
        var after = await client.GetStringAsync("/receipts");
        Assert.Contains("Grossiste de la Brasserie", after);
        Assert.DoesNotContain("Grossiste du Bistro", after);
        // Le ticket du Bistro n'est plus lisible depuis la Brasserie : le claim pilote le filtre.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/receipts/{site.BistroReceiptId}/file")).StatusCode);
        Assert.Equal(await ActiveRestaurantOfAsync(site, site.Shared), site.Brasserie.Id);
    }

    [Fact]
    public async Task BackSite_InProduction_WithoutBaseUrl_FailsToStart()
    {
        await using var site = await BackSite.CreateAsync(postgres);
        var production = site.Factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Vitrine:BaseUrl", "");
        });

        var error = Assert.ThrowsAny<Exception>(() => production.CreateClient());

        Assert.Contains("Vitrine:BaseUrl", error.ToString());
    }

    [Fact]
    public async Task Register_WhenRegistrationIsClosed_RefusesToCreateAnAccount()
    {
        // Production : l'inscription n'est ouverte que tant qu'aucun compte n'existe ; le site en a déjà.
        await using var site = await BackSite.CreateAsync(postgres, environment: "Production");
        using var client = site.CreateClient();
        var accounts = await CountUsersAsync(site);

        var page = await client.GetAsync("/Account/Register");
        var post = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "register",
            ["Input.Email"] = "intrus@chefos.test",
            ["Input.Password"] = BackSite.Password,
            ["Input.ConfirmPassword"] = BackSite.Password
        }));

        Assert.Contains(RegistrationPolicy.ClosedMessage, WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(accounts, await CountUsersAsync(site));
    }

    private static async Task<HttpResponseMessage> PostActiveRestaurantAsync(HttpClient client, string restaurantId, string? returnUrl = null)
    {
        // Le jeton anti-falsification vient du formulaire de déconnexion de la barre du haut (toute page connectée).
        var fields = new Dictionary<string, string> { ["restaurantId"] = restaurantId };
        if (returnUrl is not null)
            fields["returnUrl"] = returnUrl;
        return await client.PostAsync("/account/active-restaurant", await BackSite.FormAsync(client, "/receipts", fields));
    }

    private static async Task<int?> ActiveRestaurantOfAsync(BackSite site, ApplicationUser user)
    {
        await using var db = new ChefOsDbContext(site.Options, new FixedTenantContext(null));
        return await db.Users.Where(u => u.Id == user.Id).Select(u => u.ActiveRestaurantId).SingleAsync();
    }

    private static async Task<int> CountUsersAsync(BackSite site)
    {
        await using var db = new ChefOsDbContext(site.Options, new FixedTenantContext(null));
        return await db.Users.CountAsync();
    }
}
