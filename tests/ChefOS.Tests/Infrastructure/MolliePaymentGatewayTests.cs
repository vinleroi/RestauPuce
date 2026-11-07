using System.Net;
using System.Text.Json;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Passerelle Mollie contre un faux <see cref="HttpMessageHandler"/> : aucun appel réseau.</summary>
public sealed class MolliePaymentGatewayTests
{
    private const string Key = "test_SECRETKEY123";

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static (MolliePaymentGateway Gateway, FakeHandler Handler) Create(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(MolliePaymentGateway.BaseAddress) };
        return (new MolliePaymentGateway(http, new MollieSettings(Key)), handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Create_PostsAmountAsInvariantString_WithBearerAndMetadata()
    {
        var (gateway, handler) = Create((_, _) => Json("""{"id":"tr_1","status":"open","_links":{"checkout":{"href":"https://pay.mollie.test/c/1"}}}""", HttpStatusCode.Created));
        var metadata = new Dictionary<string, string> { ["reservationId"] = "12", ["restaurantId"] = "3" };

        var created = await gateway.CreateAsync(10m, "Réservation Dupont · 2 couverts", "https://v.test/r/abc", "https://v.test/hook", metadata, default);

        Assert.Equal(new GatewayPayment("tr_1", "https://pay.mollie.test/c/1"), created);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.mollie.com/v2/payments", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("EUR", root.GetProperty("amount").GetProperty("currency").GetString());
        Assert.Equal("10.00", root.GetProperty("amount").GetProperty("value").GetString());
        Assert.Equal("Réservation Dupont · 2 couverts", root.GetProperty("description").GetString());
        Assert.Equal("https://v.test/r/abc", root.GetProperty("redirectUrl").GetString());
        Assert.Equal("https://v.test/hook", root.GetProperty("webhookUrl").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("metadata").ValueKind);
        Assert.Equal("12", root.GetProperty("metadata").GetProperty("reservationId").GetString());
    }

    [Theory]
    [InlineData(49.5, "49.50")]
    [InlineData(7, "7.00")]
    [InlineData(1234.56, "1234.56")]
    public async Task Amounts_UseInvariantCulture_WhateverTheCurrentCulture(double amount, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-BE");
        try
        {
            var (gateway, handler) = Create((_, _) => Json("""{"id":"tr_1","_links":{"checkout":{"href":"https://pay.test/1"}}}"""));
            await gateway.CreateAsync((decimal)amount, "d", "https://r", "https://w", new Dictionary<string, string>(), default);
            using var json = JsonDocument.Parse(handler.Calls[0].Body!);
            Assert.Equal(expected, json.RootElement.GetProperty("amount").GetProperty("value").GetString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("open", GatewayStatus.Open)]
    [InlineData("pending", GatewayStatus.Open)]
    [InlineData("paid", GatewayStatus.Paid)]
    [InlineData("authorized", GatewayStatus.Paid)]
    [InlineData("failed", GatewayStatus.Failed)]
    [InlineData("expired", GatewayStatus.Expired)]
    [InlineData("canceled", GatewayStatus.Canceled)]
    public async Task GetStatus_MapsMollieStatuses(string mollie, GatewayStatus expected)
    {
        var (gateway, handler) = Create((_, _) => Json($$"""{"id":"tr_9","status":"{{mollie}}"}"""));

        Assert.Equal(expected, await gateway.GetStatusAsync("tr_9", default));

        var (request, _) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.mollie.com/v2/payments/tr_9", request.RequestUri!.ToString());
        Assert.Equal(Key, request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GetStatus_UnknownStatus_IsAnError()
    {
        var (gateway, _) = Create((_, _) => Json("""{"status":"charged_back"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetStatusAsync("tr_9", default));
        Assert.Equal("Réponse inattendue de Mollie, réessayez", ex.Message);
    }

    [Fact]
    public async Task Refund_PostsToRefundsEndpointWithAmount()
    {
        var (gateway, handler) = Create((_, _) => Json("""{"id":"re_1","status":"queued"}""", HttpStatusCode.Created));

        await gateway.RefundAsync("tr_9", 36.5m, default);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.mollie.com/v2/payments/tr_9/refunds", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("36.50", json.RootElement.GetProperty("amount").GetProperty("value").GetString());
        Assert.Equal("EUR", json.RootElement.GetProperty("amount").GetProperty("currency").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Mollie a refusé l'authentification (clé API invalide)")]
    [InlineData(HttpStatusCode.NotFound, "Paiement introuvable chez Mollie")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Mollie a refusé la demande (données invalides)")]
    [InlineData(HttpStatusCode.TooManyRequests, "Mollie est momentanément saturé, réessayez")]
    [InlineData(HttpStatusCode.BadGateway, "Mollie est momentanément indisponible, réessayez")]
    [InlineData(HttpStatusCode.Conflict, "Mollie a refusé la demande (code 409)")]
    public async Task HttpErrors_AreTranslated_WithoutKeyOrBody(HttpStatusCode code, string message)
    {
        var (gateway, _) = Create((_, _) => Json($$"""{"status":{{(int)code}},"title":"Boom","detail":"clé {{Key}} rejetée"}""", code));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetStatusAsync("tr_1", default));

        Assert.Equal(message, ex.Message);
        Assert.DoesNotContain(Key, ex.ToString());
    }

    [Fact]
    public async Task Timeout_AndNetworkErrors_AreTranslated()
    {
        var (timeout, _) = Create((_, _) => throw new TaskCanceledException("délai", new TimeoutException()));
        var (network, _) = Create((_, _) => throw new HttpRequestException($"échec vers https://api.mollie.com avec {Key}"));

        var t = await Assert.ThrowsAsync<InvalidOperationException>(() => timeout.GetStatusAsync("tr_1", default));
        var n = await Assert.ThrowsAsync<InvalidOperationException>(() => network.GetStatusAsync("tr_1", default));

        Assert.Equal("Mollie ne répond pas (délai dépassé), réessayez", t.Message);
        Assert.Equal("Impossible de joindre Mollie, réessayez", n.Message);
        Assert.DoesNotContain(Key, n.ToString());
    }

    [Fact]
    public async Task CallerCancellation_IsNotTranslated()
    {
        var (gateway, _) = Create((_, _) => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetStatusAsync("tr_1", cts.Token));
    }

    [Fact]
    public void Settings_NeverPrintTheKey() => Assert.DoesNotContain(Key, new MollieSettings(Key).ToString());

    // ── Passerelles de démonstration et enregistrement ───────────────────────

    [Fact]
    public async Task Demo_CheckoutUrl_StatusAndPartialRefunds()
    {
        var gateway = new DemoPaymentGateway("https://vitrine.test/");

        var created = await gateway.CreateAsync(30m, "d", "https://back", "https://hook", new Dictionary<string, string>(), default);

        Assert.StartsWith("https://vitrine.test/paiement-demo/demo_", created.CheckoutUrl);
        Assert.EndsWith(created.Id, created.CheckoutUrl);
        Assert.Equal(GatewayStatus.Open, await gateway.GetStatusAsync(created.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(created.Id, 10m, default));
        gateway.SetStatus(created.Id, GatewayStatus.Paid);
        await gateway.RefundAsync(created.Id, 10m, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(created.Id, 25m, default));
        Assert.Equal(10m, gateway.RefundedAmount(created.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetStatusAsync("inconnu", default));
    }

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ChefOS.Infrastructure.Data.ITenantContext>(new ChefOS.Infrastructure.Data.FixedTenantContext(null));
        services.AddChefOsInfrastructure("Host=localhost;Port=1;Database=chefos_unused;Username=u;Password=p");
        services.AddChefOsPayments(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Registration_DefaultIsDemo_WithServicesAndEmail()
    {
        using var provider = Build(("Payments:BaseUrl", "https://vitrine.test"));
        using var scope = provider.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();
        Assert.IsType<DemoPaymentGateway>(gateway);
        Assert.Same(gateway, scope.ServiceProvider.GetRequiredService<DemoPaymentGateway>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReservationService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PaymentService>());
        Assert.IsType<ChefOS.Infrastructure.Notifications.LoggingEmailSender>(scope.ServiceProvider.GetRequiredService<ChefOS.Infrastructure.Notifications.IEmailSender>());
    }

    [Fact]
    public void Registration_MollieWithKey_UsesTypedClient()
    {
        using var provider = Build(("Payments:Gateway", "Mollie"), ("Mollie:ApiKey", Key));
        using var scope = provider.CreateScope();

        Assert.IsType<MolliePaymentGateway>(scope.ServiceProvider.GetRequiredService<IPaymentGateway>());
    }

    [Fact]
    public async Task Registration_MollieWithoutKey_FailsOnUseWithExplicitMessage()
    {
        var previous = Environment.GetEnvironmentVariable("MOLLIE_API_KEY");
        Environment.SetEnvironmentVariable("MOLLIE_API_KEY", null);
        try
        {
            using var provider = Build(("Payments:Gateway", "mollie"));
            using var scope = provider.CreateScope();
            var gateway = scope.ServiceProvider.GetRequiredService<IPaymentGateway>();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.CreateAsync(10m, "d", "https://r", "https://w", new Dictionary<string, string>(), default));
            Assert.StartsWith("Clé API Mollie non configurée", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOLLIE_API_KEY", previous);
        }
    }

    [Fact]
    public void Registration_MollieKeyFromEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("MOLLIE_API_KEY");
        Environment.SetEnvironmentVariable("MOLLIE_API_KEY", Key);
        try
        {
            using var provider = Build(("Payments:Gateway", "Mollie"));
            using var scope = provider.CreateScope();
            Assert.IsType<MolliePaymentGateway>(scope.ServiceProvider.GetRequiredService<IPaymentGateway>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOLLIE_API_KEY", previous);
        }
    }
}
