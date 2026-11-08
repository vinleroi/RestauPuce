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
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);
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
        await h.Demo.SetStatusAsync(firstPayment.GatewayPaymentId!, GatewayStatus.Paid);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.Payments.SyncAsync(firstPayment.GatewayPaymentId!)));

        var reservation = await h.LoadAsync(first.Token);
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Equal("Paiement reçu après expiration : remboursé", reservation.Note);
        var payment = (await h.PaymentsOfAsync(reservation.Id)).Single();
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(40m, payment.RefundAmount);
        Assert.NotNull(payment.RefundedAt);
        Assert.Equal(40m, await h.Demo.RefundedAmountAsync(firstPayment.GatewayPaymentId!));
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
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);

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

    [Theory]
    [InlineData(ReservationStatus.Expired, "Paiement reçu après expiration : remboursé")]
    [InlineData(ReservationStatus.Cancelled, "Paiement reçu après annulation : remboursé")]
    public async Task Payment_StatusFlipsBetweenReadAndConfirm_IsRefundedNeverLeftPaid(ReservationStatus flip, string note)
    {
        var h = await ReservationHarness.CreateAsync(postgres, capacity: 4);
        var start = await h.CreateAsync(Request(4));
        var reservation = await h.LoadAsync(start.Token);
        var payment = (await h.PaymentsOfAsync(reservation.Id)).Single();
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);

        // Une autre transaction (expiration paresseuse, annulation du client) change le statut, non validée, pendant la synchronisation.
        await using var other = h.Open();
        await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Reservations.Where(r => r.Id == reservation.Id).ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, flip));
        if (flip == ReservationStatus.Expired)
        {
            // la place libérée est aussitôt reprise par un autre client
            other.Reservations.Add(new Reservation
            {
                PublicToken = "autre-client-" + Guid.NewGuid().ToString("N"), StartsAtUtc = reservation.StartsAtUtc, ServiceName = reservation.ServiceName,
                PartySize = 4, CustomerName = "Autre", CustomerEmail = "a@b.be", CustomerPhone = "123456", Status = ReservationStatus.Confirmed,
                CreatedAt = h.Clock.GetUtcNow().UtcDateTime, IsManual = true
            });
            await other.SaveChangesAsync();
        }

        var sync = Task.Run(() => h.Payments.SyncAsync(payment.GatewayPaymentId!));
        await Task.Delay(1000);
        Assert.False(sync.IsCompleted); // attend le verrou de ligne de la réservation
        await transaction.CommitAsync();
        await sync;

        var after = await h.LoadAsync(start.Token);
        Assert.Equal(flip, after.Status);
        Assert.Equal(note, after.Note);
        var refunded = (await h.PaymentsOfAsync(reservation.Id)).Single();
        Assert.Equal(PaymentStatus.Refunded, refunded.Status);
        Assert.Equal(1, h.Gateway.RefundCalls);
    }

    [Fact]
    public async Task Refund_UsesIdempotencyKey_AndSecondCallWhileInProgressBacksOff()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        var release = new TaskCompletionSource();
        h.Gateway.RefundBlock = release.Task;

        var first = Task.Run(() => h.Payments.RefundAsync(payment.Id));
        while (h.Gateway.RefundCalls == 0)
            await Task.Delay(20);
        await h.Payments.RefundAsync(payment.Id); // paiement RefundPending, remboursement en cours ailleurs
        Assert.Equal(1, h.Gateway.RefundCalls);
        release.SetResult();
        await first;

        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);
        Assert.Equal([$"refund-{payment.GatewayPaymentId}"], h.Gateway.RefundKeys);
    }

    [Fact]
    public async Task Refund_GatewaySucceededButCallFailed_RetryReconcilesWithoutRefundingAgain()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        h.Gateway.FailAfterRefund = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Payments.RefundAsync(payment.Id));
        Assert.Equal(PaymentStatus.RefundPending, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);

        h.Gateway.FailAfterRefund = false;
        await h.Payments.RefundAsync(payment.Id);

        var after = (await h.PaymentsOfAsync(payment.ReservationId)).Single();
        Assert.Equal(PaymentStatus.Refunded, after.Status);
        Assert.Equal(1, h.Gateway.RefundCalls);
        Assert.Equal(20m, await h.Demo.RefundedAmountAsync(payment.GatewayPaymentId!));
    }

    [Fact]
    public async Task Payment_Confirmation_LogsConfirmationEmail()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        Assert.Empty(h.Email.Sent);

        await h.PayAsync(start.Token);
        await h.Payments.SyncAsync((await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single().GatewayPaymentId!); // rejeu

        var sent = Assert.Single(h.Email.Sent);
        Assert.Equal("dupont@example.com", sent.To);
        Assert.Contains("confirmée", sent.Subject);
    }
}
