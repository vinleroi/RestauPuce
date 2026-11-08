using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ChefOS.Tests.Infrastructure;

namespace ChefOS.Tests.Vitrine;

/// <summary>Le site public de bout en bout (HTTP), sur la passerelle Demo.</summary>
[Collection("postgres")]
public sealed class VitrineSiteTests(PostgresFixture postgres)
{
    /// <summary>Blazor encode les caractères non ASCII en entités (« &amp;#xE9; ») : on décode avant de chercher du texte français.</summary>
    private static async Task<string> Html(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Vitrine_Unpublished_Returns404()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        var client = site.Client;
        var slug = site.Hidden.Slug;

        foreach (var path in new[] { $"/{slug}", $"/{slug}/vins", $"/{slug}/reserver", $"/{slug}/reservation/abc", $"/{slug}/images/{site.HiddenImageId}" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/slug-inconnu")).StatusCode);
    }

    [Fact]
    public async Task Vitrine_Published_ShowsMenuAndWines()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        var client = site.Client;

        var home = await client.GetAsync($"/{site.Bistro.Slug}");
        var html = await Html(home);

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("<html lang=\"fr-BE\">", html);
        Assert.Contains("<title>Le Bistro Test — Cuisine belge de saison</title>", html);
        Assert.Contains("<meta name=\"description\" content=\"Cuisine belge de saison\"", html);
        Assert.Contains("property=\"og:title\"", html);
        Assert.Contains($"property=\"og:image\" content=\"http://localhost/{site.Bistro.Slug}/images/{site.BistroImageId}\"", html);
        Assert.Contains("Waterzooi de poulet", html);
        Assert.Contains("Poulet fermier, légumes, crème", html);
        Assert.Contains("Chablis Premier Cru", html);
        Assert.Contains("Réserver une table", html);
        Assert.Contains("Toute la carte des vins", html);
        Assert.Contains("Une salle chaleureuse", html);
        Assert.DoesNotContain("Plat de l'autre maison", html);
        Assert.DoesNotContain("Vin de l'autre maison", html);

        var wines = await Html(await client.GetAsync($"/{site.Bistro.Slug}/vins"));
        Assert.Contains("Chablis Premier Cru", wines);
        Assert.DoesNotContain("Vin de l'autre maison", wines);

        var detail = await client.GetAsync($"/{site.Bistro.Slug}/vins/{site.BistroWineId}");
        var fiche = await Html(detail);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("Agrumes et pierre à fusil", fiche);
        Assert.Contains($"#plat-{site.BistroDishId}", fiche);
    }

    [Fact]
    public async Task Wines_FilteredByColor_KeepsOnlyThatColor()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);

        var rouge = await Html(await site.Client.GetAsync($"/{site.Bistro.Slug}/vins?couleur=Rouge"));
        var blanc = await Html(await site.Client.GetAsync($"/{site.Bistro.Slug}/vins?couleur=Blanc"));

        Assert.DoesNotContain("Chablis Premier Cru", rouge);
        Assert.Contains("Aucun vin ne correspond", rouge);
        Assert.Contains("Chablis Premier Cru", blanc);
    }

    [Fact]
    public async Task WineOfAnotherRestaurantOrUnknown_Returns404StatusCode()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);

        // L'identifiant du vin du Bistro, demandé sous le slug d'un autre restaurant : jamais la fiche, et un vrai 404.
        var foreign = await site.Client.GetAsync($"/{site.Other.Slug}/vins/{site.BistroWineId}");
        var unknown = await site.Client.GetAsync($"/{site.Bistro.Slug}/vins/999999");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("Agrumes et pierre à fusil", await Html(foreign));
    }

    [Fact]
    public async Task HomeHeroLinks_StayOnTheRestaurantPage_NotTheRootPage()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);

        var html = await Html(await site.Client.GetAsync($"/{site.Bistro.Slug}"));

        // Avec <base href="/">, « #carte » seul mènerait à « /#carte » (la page d'accueil générale).
        Assert.Contains($"href=\"/{site.Bistro.Slug}#carte\"", html);
        Assert.Contains($"href=\"/{site.Bistro.Slug}#ambiance\"", html);
        Assert.DoesNotContain("href=\"#", html);
    }

    [Fact]
    public async Task ReservationPageAndDemoCheckout_AreNotIndexed()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        string id;
        await using (var scope = site.Factory.Services.CreateAsyncScope())
        {
            var gateway = scope.ServiceProvider.GetRequiredService<ChefOS.Infrastructure.Payments.DemoPaymentGateway>();
            id = (await gateway.CreateAsync(20m, "d", $"http://localhost/{site.Bistro.Slug}/reservation/abc", "", new Dictionary<string, string>(), default)).Id;
        }

        var reservation = await Html(await site.Client.GetAsync($"/{site.Bistro.Slug}/reservation/jeton-inconnu"));
        var checkout = await Html(await site.Client.GetAsync($"/paiement-demo/{id}"));
        var home = await Html(await site.Client.GetAsync($"/{site.Bistro.Slug}"));

        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", reservation);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", checkout);
        Assert.DoesNotContain("noindex", home);
    }

    [Fact]
    public async Task Images_OtherRestaurant_404()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        var client = site.Client;

        var own = await client.GetAsync($"/{site.Bistro.Slug}/images/{site.BistroImageId}");
        var foreign = await client.GetAsync($"/{site.Other.Slug}/images/{site.BistroImageId}");
        var hidden = await client.GetAsync($"/{site.Hidden.Slug}/images/{site.HiddenImageId}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("image/png", own.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=86400", own.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", own.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Webhook_UnknownSlug_404()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "tr_123" });

        var unknown = await site.Client.PostAsync("/slug-inconnu/webhooks/mollie", form);
        var unpublished = await site.Client.PostAsync($"/{site.Hidden.Slug}/webhooks/mollie", form);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
    }

    [Fact]
    public async Task Webhook_KnownSlug_AlwaysAnswers200()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        var path = $"/{site.Bistro.Slug}/webhooks/mollie";

        var unknownPayment = await site.Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "tr_inconnu" }));
        var noForm = await site.Client.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        var missingId = await site.Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.OK, unknownPayment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noForm.StatusCode);
        Assert.Equal(HttpStatusCode.OK, missingId.StatusCode);
    }

    /// <summary>Passerelle dont la lecture du statut échoue toujours (panne chez Mollie) ; aucun appel réseau.</summary>
    private sealed class UnreachableGateway : ChefOS.Infrastructure.Payments.IPaymentGateway
    {
        public string Name => "Demo";

        public Task<ChefOS.Infrastructure.Payments.GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
            IDictionary<string, string> metadata, CancellationToken ct) => throw new InvalidOperationException("injoignable");

        public Task<ChefOS.Infrastructure.Payments.GatewayStatus> GetStatusAsync(string id, CancellationToken ct) => throw new InvalidOperationException("injoignable");

        public Task RefundAsync(string id, decimal amount, string idempotencyKey, CancellationToken ct) => throw new InvalidOperationException("injoignable");

        public Task<decimal> GetRefundedAmountAsync(string id, CancellationToken ct) => throw new InvalidOperationException("injoignable");
    }

    [Fact]
    public async Task Webhook_SyncFailure_Answers503SoMollieRetries()
    {
        await using var site = await VitrineSite.CreateAsync(postgres, configureServices: services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(ChefOS.Infrastructure.Payments.IPaymentGateway)).ToList())
                services.Remove(d);
            services.AddScoped<ChefOS.Infrastructure.Payments.IPaymentGateway, UnreachableGateway>();
        });
        await using (var db = new ChefOS.Infrastructure.Data.ChefOsDbContext(site.Options, new ChefOS.Infrastructure.Data.FixedTenantContext(site.Bistro.Id)))
        {
            var reservation = new ChefOS.Core.Vitrine.Reservation
            {
                PublicToken = "jeton-webhook", StartsAtUtc = DateTime.UtcNow.AddDays(3), ServiceName = "Dîner", PartySize = 2, CustomerName = "Dupont",
                CustomerEmail = "d@example.com", CustomerPhone = "0470 12 34 56", Status = ChefOS.Core.Vitrine.ReservationStatus.PendingPayment,
                CreatedAt = DateTime.UtcNow, AmountDue = 20m
            };
            db.Reservations.Add(reservation);
            await db.SaveChangesAsync();
            db.Payments.Add(new ChefOS.Core.Vitrine.Payment
            {
                ReservationId = reservation.Id, Kind = ChefOS.Core.Vitrine.PaymentKind.Deposit, Amount = 20m, Status = ChefOS.Core.Vitrine.PaymentStatus.Open,
                Gateway = "Demo", GatewayPaymentId = "tr_en_panne", CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var failing = await site.Client.PostAsync($"/{site.Bistro.Slug}/webhooks/mollie",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "tr_en_panne" }));
        var unknown = await site.Client.PostAsync($"/{site.Bistro.Slug}/webhooks/mollie",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "tr_inconnu" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }

    [Fact]
    public async Task Webhook_IsNotRateLimited()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        var path = $"/{site.Bistro.Slug}/webhooks/mollie";

        for (var i = 0; i < 15; i++)
        {
            var response = await site.Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "tr_x" }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task DemoCheckout_NotDemoMode_404()
    {
        await using var site = await VitrineSite.CreateAsync(postgres, gateway: "Mollie");

        var response = await site.Client.GetAsync("/paiement-demo/demo_abc");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DemoCheckout_DemoMode_UnknownPayment_404_AndKnownOneShowsAmountAndButtons()
    {
        await using var site = await VitrineSite.CreateAsync(postgres);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client.GetAsync("/paiement-demo/demo_inconnu")).StatusCode);

        string id;
        await using (var scope = site.Factory.Services.CreateAsyncScope())
        {
            var gateway = scope.ServiceProvider.GetRequiredService<ChefOS.Infrastructure.Payments.DemoPaymentGateway>();
            id = (await gateway.CreateAsync(20m, "Réservation Dupont · 2 couverts", $"http://localhost/{site.Bistro.Slug}/reservation/abc", "",
                new Dictionary<string, string>(), default)).Id;
        }

        var response = await site.Client.GetAsync($"/paiement-demo/{id}");
        var html = await Html(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Réservation Dupont · 2 couverts", html);
        Assert.Contains("20", html);
        Assert.Contains(">Payer<", html);
        Assert.Contains("Simuler un échec", html);
    }
}
