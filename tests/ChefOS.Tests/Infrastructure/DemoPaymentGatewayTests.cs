using ChefOS.Infrastructure.Payments;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Passerelle de démonstration persistée : table globale partagée entre le back et la vitrine.</summary>
[Collection("postgres")]
public sealed class DemoPaymentGatewayTests(PostgresFixture postgres)
{
    private static readonly IDictionary<string, string> NoMetadata = new Dictionary<string, string>();

    private async Task<DemoPaymentGateway> CreateGatewayAsync(string baseUrl = "https://vitrine.test/")
    {
        var options = await postgres.CreateDatabaseAsync();
        return new DemoPaymentGateway(baseUrl, new TestContextFactory(options, null));
    }

    [Fact]
    public async Task CheckoutUrl_StatusAndPartialRefunds()
    {
        var gateway = await CreateGatewayAsync();

        var created = await gateway.CreateAsync(30m, "d", "https://back", "https://hook", NoMetadata, default);

        Assert.StartsWith("https://vitrine.test/paiement-demo/demo_", created.CheckoutUrl);
        Assert.EndsWith(created.Id, created.CheckoutUrl);
        Assert.Equal(GatewayStatus.Open, await gateway.GetStatusAsync(created.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(created.Id, 10m, "k0", default));
        await gateway.SetStatusAsync(created.Id, GatewayStatus.Paid);
        await gateway.RefundAsync(created.Id, 10m, "k1", default);
        await gateway.RefundAsync(created.Id, 10m, "k1", default); // même clé : sans effet
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(created.Id, 25m, "k2", default));
        Assert.Equal(10m, await gateway.RefundedAmountAsync(created.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetStatusAsync("inconnu", default));
    }

    [Fact]
    public async Task Refund_RefusedByChecks_DoesNotRecordTheIdempotencyKey()
    {
        var gateway = await CreateGatewayAsync();
        var created = await gateway.CreateAsync(30m, "d", "https://back", "https://hook", NoMetadata, default);

        // Refusé car non réglé : la clé « k » ne doit pas être retenue.
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(created.Id, 10m, "k", default));
        await gateway.SetStatusAsync(created.Id, GatewayStatus.Paid);

        await gateway.RefundAsync(created.Id, 10m, "k", default); // la même clé fonctionne maintenant
        Assert.Equal(10m, await gateway.RefundedAmountAsync(created.Id));
        Assert.Equal(["k"], (await gateway.FindAsync(created.Id))!.RefundKeys);
    }

    [Fact]
    public async Task Refund_AboveAmount_DoesNotRecordTheKeyEither()
    {
        var gateway = await CreateGatewayAsync();
        var created = await gateway.CreateAsync(30m, "d", "https://back", "https://hook", NoMetadata, default);
        await gateway.SetStatusAsync(created.Id, GatewayStatus.Paid);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RefundAsync(created.Id, 40m, "k", default));
        await gateway.RefundAsync(created.Id, 30m, "k", default);

        Assert.Equal(30m, await gateway.RefundedAmountAsync(created.Id));
    }

    [Fact]
    public async Task Payments_AreSharedBetweenGatewayInstances_AndKeepTheirDetails()
    {
        var options = await postgres.CreateDatabaseAsync();
        var factory = new TestContextFactory(options, null);
        var vitrine = new DemoPaymentGateway("https://vitrine.test", factory);
        var back = new DemoPaymentGateway("https://vitrine.test", factory);

        var created = await vitrine.CreateAsync(12.5m, "Réservation Dupont · 2 couverts", "https://vitrine.test/le-bistro/reservation/abc", "", NoMetadata, default);
        await vitrine.SetStatusAsync(created.Id, GatewayStatus.Paid);
        await back.RefundAsync(created.Id, 12.5m, "refund-1", default);

        var seen = await vitrine.FindAsync(created.Id);
        Assert.NotNull(seen);
        Assert.Equal(12.5m, seen.Amount);
        Assert.Equal("Réservation Dupont · 2 couverts", seen.Description);
        Assert.Equal("https://vitrine.test/le-bistro/reservation/abc", await back.GetRedirectUrlAsync(created.Id));
        Assert.Equal(12.5m, await vitrine.GetRefundedAmountAsync(created.Id, default));
        Assert.Null(await vitrine.FindAsync("inconnu"));
    }

    [Fact]
    public async Task ConcurrentRefundsWithTheSameKey_RefundOnce()
    {
        var gateway = await CreateGatewayAsync();
        var created = await gateway.CreateAsync(30m, "d", "https://back", "https://hook", NoMetadata, default);
        await gateway.SetStatusAsync(created.Id, GatewayStatus.Paid);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gateway.RefundAsync(created.Id, 30m, "same", default)));

        Assert.Equal(30m, await gateway.RefundedAmountAsync(created.Id));
    }
}
