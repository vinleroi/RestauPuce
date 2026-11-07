using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using static ChefOS.Tests.Infrastructure.ReservationHarness;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class PaymentServiceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Payment_Sync_Idempotent()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        Assert.Equal(PaymentStatus.Open, await h.Payments.SyncAsync(payment.GatewayPaymentId!)); // rien n'a changé chez la passerelle
        h.Demo.SetStatus(payment.GatewayPaymentId!, GatewayStatus.Paid);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => h.Payments.SyncAsync(payment.GatewayPaymentId!)));
        await h.Payments.SyncAsync(payment.GatewayPaymentId!);

        Assert.All(results, r => Assert.Equal(PaymentStatus.Paid, r));
        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        var confirmedAt = reservation.ConfirmedAt;
        Assert.Equal(confirmedAt, (await h.LoadAsync(start.Token)).ConfirmedAt);
        Assert.Null(await h.Payments.SyncAsync("inconnu"));
        Assert.Equal(0, h.Gateway.RefundCalls);
    }

    [Fact]
    public async Task Payment_PaidAfterExpiryWithRoom_Confirmed()
    {
        var h = await ReservationHarness.CreateAsync(postgres, capacity: 10);
        var start = await h.CreateAsync(Request(4));
        h.Clock.Advance(TimeSpan.FromMinutes(45));
        await h.Reservations.ExpireStaleAsync();
        Assert.Equal(ReservationStatus.Expired, (await h.LoadAsync(start.Token)).Status);

        await h.PayAsync(start.Token);

        Assert.Equal(ReservationStatus.Confirmed, (await h.LoadAsync(start.Token)).Status);
        Assert.Equal(0, h.Gateway.RefundCalls);
    }

    [Fact]
    public async Task Payment_PaidAfterExpiryAndSeatTaken_Refunded()
    {
        var h = await ReservationHarness.CreateAsync(postgres, capacity: 4);
        var first = await h.CreateAsync(Request(4, name: "Premier"));
        var firstPayment = (await h.PaymentsOfAsync((await h.LoadAsync(first.Token)).Id)).Single();
        h.Clock.Advance(TimeSpan.FromMinutes(31)); // la première réservation expire, sa place est libre
        var second = await h.CreateAsync(Request(4, name: "Second", email: "second@example.com"));
        await h.PayAsync(second.Token);
        Assert.Equal(ReservationStatus.Confirmed, (await h.LoadAsync(second.Token)).Status);

        // le premier client paie quand même : webhook et retour du client en même temps
        h.Demo.SetStatus(firstPayment.GatewayPaymentId!, GatewayStatus.Paid);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.Payments.SyncAsync(firstPayment.GatewayPaymentId!)));

        var reservation = await h.LoadAsync(first.Token);
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Equal("Paiement reçu après expiration : remboursé", reservation.Note);
        var payment = (await h.PaymentsOfAsync(reservation.Id)).Single();
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(40m, payment.RefundAmount);
        Assert.NotNull(payment.RefundedAt);
        Assert.Equal(40m, h.Demo.RefundedAmount(firstPayment.GatewayPaymentId!));
        Assert.Equal(1, h.Gateway.RefundCalls);
        Assert.Equal(ReservationStatus.Confirmed, (await h.LoadAsync(second.Token)).Status);
        await using var db = h.Open();
        Assert.Equal(4, await db.Reservations.Where(r => r.Status == ReservationStatus.Confirmed).SumAsync(r => r.PartySize));
    }

    [Fact]
    public async Task Payment_PaidAfterExpiry_RefundFailureLeavesRefundPending()
    {
        var h = await ReservationHarness.CreateAsync(postgres, capacity: 4);
        var first = await h.CreateAsync(Request(4, name: "Premier"));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(first.Token)).Id)).Single();
        h.Clock.Advance(TimeSpan.FromMinutes(31));
        await h.CreateAsync(Request(4, name: "Second", email: "s@example.com"));
        h.Gateway.FailRefund = true;
        h.Demo.SetStatus(payment.GatewayPaymentId!, GatewayStatus.Paid);

        await h.Payments.SyncAsync(payment.GatewayPaymentId!);

        Assert.Equal(PaymentStatus.RefundPending, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);
        h.Gateway.FailRefund = false;
        await h.Payments.RefundAsync(payment.Id);
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);
    }

    [Fact]
    public async Task Payment_PaidAfterCustomerCancellation_IsRefundedNotRevived()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.Reservations.CancelByCustomerAsync(start.Token);

        await h.PayAsync(start.Token);

        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(reservation.Id)).Single().Status);
    }

    [Fact]
    public async Task Refund_OnlyForPaidPayments_AndUnknownRefused()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        await Assert.ThrowsAsync<DomainValidationException>(() => h.Payments.RefundAsync(payment.Id)); // jamais réglé
        await Assert.ThrowsAsync<DomainValidationException>(() => h.Payments.RefundAsync(987654));
        Assert.Equal(0, h.Gateway.RefundCalls);
    }

    [Fact]
    public async Task Refund_Concurrent_CallsGatewayOnce()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.Payments.RefundAsync(payment.Id)));

        Assert.Equal(1, h.Gateway.RefundCalls);
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);
    }
}
