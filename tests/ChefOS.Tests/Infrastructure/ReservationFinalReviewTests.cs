using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using static ChefOS.Tests.Infrastructure.ReservationHarness;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Correctifs de la revue finale de l'étape 4 : règle d'annulation figée, récupération des paiements, note de cuisine, remboursements.</summary>
[Collection("postgres")]
public class ReservationFinalReviewTests(PostgresFixture postgres)
{
    private static async Task SetCancellationHoursAsync(ReservationHarness h, int hours)
    {
        await using var db = h.Open();
        await db.VitrineProfiles.ExecuteUpdateAsync(s => s.SetProperty(p => p.FreeCancellationHours, hours));
    }

    // ── I2 : la règle d'annulation est figée à la réservation ────────────────

    [Fact]
    public async Task FreeCancellation_IsFrozenAtBooking_StrictenedRuleDoesNotChangeIt()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2)); // règle de 24 h
        await h.PayAsync(start.Token);
        var id = (await h.LoadAsync(start.Token)).Id;
        var expected = new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc); // 24 h avant le vendredi 18:00 UTC

        await SetCancellationHoursAsync(h, 72);

        Assert.Equal(expected, (await h.LoadAsync(start.Token)).FreeCancellationUntilUtc);
        Assert.Equal(expected, (await h.Reservations.GetByTokenAsync(start.Token))!.FreeCancellationUntilUtc);

        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 18, 30, 0, TimeSpan.Zero)); // hors des 24 h acceptées, dans les 72 h d'aujourd'hui
        await h.Reservations.CancelByCustomerAsync(start.Token);

        Assert.Equal(PaymentStatus.Paid, (await h.PaymentsOfAsync(id)).Single().Status);
        Assert.Equal(0, h.Gateway.RefundCalls);
    }

    [Fact]
    public async Task FreeCancellation_IsFrozenAtBooking_RelaxedRuleDoesNotChangeIt()
    {
        var h = await ReservationHarness.CreateAsync(postgres, profile: p => p.FreeCancellationHours = 72);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        await SetCancellationHoursAsync(h, 0);

        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)); // 78 h avant : dans les 72 h acceptées, hors de la règle d aujourd hui (0)
        await h.Reservations.CancelByCustomerAsync(start.Token);

        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc), (await h.Reservations.GetByTokenAsync(start.Token))!.FreeCancellationUntilUtc);
    }

    [Fact]
    public async Task FreeCancellation_ManualReservationGetsTheCurrentRule()
    {
        var h = await ReservationHarness.CreateAsync(postgres, profile: p => p.FreeCancellationHours = 48);

        var created = await h.Reservations.CreateManualAsync(Request(2), force: false);

        await using var db = h.Open();
        var reservation = await db.Reservations.SingleAsync(r => r.Id == created.ReservationId);
        Assert.Equal(reservation.StartsAtUtc.AddHours(-48), reservation.FreeCancellationUntilUtc);
    }

    // ── I4 : le back récupère un paiement ouvert dont il n'a jamais entendu parler ─

    [Fact]
    public async Task ListForDay_SyncsAnOpenPaymentOfAPendingReservation()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid); // le webhook n'est jamais arrivé

        var day = await h.Reservations.ListForDayAsync(Friday);

        var card = day.Single().Reservations.Single();
        Assert.Equal(ReservationStatus.Confirmed, card.Status);
        Assert.Equal(PaymentStatus.Paid, card.Payments.Single().Status);
    }

    [Fact]
    public async Task ListForDay_SyncsAnOpenPaymentOfAnExpiredReservation()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        h.Clock.Advance(TimeSpan.FromMinutes(45));
        await h.Reservations.ExpireStaleAsync();
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);

        var day = await h.Reservations.ListForDayAsync(Friday);

        Assert.Equal(ReservationStatus.Confirmed, day.Single().Reservations.Single().Status); // la place était libre
    }

    [Fact]
    public async Task ListForDay_GatewayFailure_StillListsTheDay()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);
        h.Gateway.FailStatus = true;

        var day = await h.Reservations.ListForDayAsync(Friday);

        Assert.Equal(ReservationStatus.PendingPayment, day.Single().Reservations.Single().Status);
        Assert.Equal(PaymentStatus.Open, day.Single().Reservations.Single().Payments.Single().Status);
    }

    [Fact]
    public async Task ListForDay_SyncsAtMostTenPaymentsPerCall()
    {
        var h = await ReservationHarness.CreateAsync(postgres, capacity: 30);
        for (var i = 0; i < 12; i++)
        {
            var start = await h.CreateAsync(Request(1, name: $"Client {i}", email: $"c{i}@example.com"));
            var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
            await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);
        }

        var first = await h.Reservations.ListForDayAsync(Friday);
        Assert.Equal(10, first.Single().Reservations.Count(r => r.Status == ReservationStatus.Confirmed));

        var second = await h.Reservations.ListForDayAsync(Friday);
        Assert.Equal(12, second.Single().Reservations.Count(r => r.Status == ReservationStatus.Confirmed));
    }

    [Fact]
    public async Task SyncById_AppliesThePaymentAndRefusesUnknownIds()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        Assert.Equal(PaymentStatus.Open, await h.Payments.SyncByIdAsync(payment.Id));
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);
        Assert.Equal(PaymentStatus.Paid, await h.Payments.SyncByIdAsync(payment.Id));

        Assert.Equal(ReservationStatus.Confirmed, (await h.LoadAsync(start.Token)).Status);
        Assert.Equal("Paiement introuvable", (await Assert.ThrowsAsync<DomainValidationException>(() => h.Payments.SyncByIdAsync(99999))).Message);
    }

    // ── I5 : la commande de cuisine dit ce qui a été payé en ligne ───────────

    [Fact]
    public async Task Seat_OrderNoteSaysWhatWasPaidOnline()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak", 25m);
        var start = await h.CreateAsync(Request(6, note: "Anniversaire", guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));
        await h.PayAsync(start.Token); // prépaiement de 30 % de 150 €
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        await h.Reservations.SeatAsync((await h.LoadAsync(start.Token)).Id, ReservationStatus.Confirmed);

        await using var db = h.Open();
        var order = await db.Orders.SingleAsync();
        Assert.Equal($"Réservation Dupont · 6 couverts · Anniversaire · Déjà réglé en ligne : {payment.Amount:0.00} € (prépaiement)".Replace('.', ','), order.Note);
        Assert.Equal(45m, payment.Amount);
    }

    [Fact]
    public async Task Seat_NothingPaidOnline_NoteHasNoPaidLine()
    {
        var h = await ReservationHarness.CreateAsync(postgres, profile: p => { p.DepositPerPerson = 0m; p.PrepaymentPercent = 0m; });
        var steak = await h.AddDishAsync("Steak", 25m);
        var start = await h.CreateAsync(Request(6, guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));

        await h.Reservations.SeatAsync((await h.LoadAsync(start.Token)).Id, ReservationStatus.Confirmed);

        await using var db = h.Open();
        Assert.Equal("Réservation Dupont · 6 couverts", (await db.Orders.SingleAsync()).Note);
    }

    // ── Remboursements ───────────────────────────────────────────────────────

    [Fact]
    public async Task Refund_IdempotencyKeyIsTheGatewayPaymentId()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        await h.Reservations.CancelByCustomerAsync(start.Token);

        Assert.Equal([$"refund-{payment.GatewayPaymentId}"], h.Gateway.RefundKeys);
    }

    [Fact]
    public async Task Refund_OnlyTheRemainderIsRequested()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2)); // 20 €
        await h.PayAsync(start.Token);
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        await h.Demo.RefundAsync(payment.GatewayPaymentId!, 5m, "remboursement-partiel", CancellationToken.None); // déjà remboursé 5 € depuis le tableau de la passerelle

        await h.Reservations.CancelByCustomerAsync(start.Token);

        Assert.Equal([15m], h.Gateway.RefundAmounts);
        Assert.Equal(20m, await h.Demo.RefundedAmountAsync(payment.GatewayPaymentId!));
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);
    }

    [Fact]
    public async Task Refund_NothingRemaining_SkipsTheGatewayAndMarksRefunded()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();
        await h.Demo.RefundAsync(payment.GatewayPaymentId!, 20m, "tout-rembourse", CancellationToken.None);

        await h.Reservations.CancelByCustomerAsync(start.Token);

        Assert.Equal(0, h.Gateway.RefundCalls);
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(payment.ReservationId)).Single().Status);
    }

    // ── Passerelle différente de celle du paiement ───────────────────────────

    [Fact]
    public async Task GatewayMismatch_RefundAndSyncRefuseWithoutCallingTheGateway()
    {
        var h = await ReservationHarness.CreateAsync(postgres);
        var paid = await h.CreateAsync(Request(2));
        await h.PayAsync(paid.Token);
        var open = await h.CreateAsync(Request(2, name: "Ouvert", email: "o@example.com"));
        var paidPayment = (await h.PaymentsOfAsync((await h.LoadAsync(paid.Token)).Id)).Single();
        var openPayment = (await h.PaymentsOfAsync((await h.LoadAsync(open.Token)).Id)).Single();
        await using (var db = h.Open())
            await db.Payments.ExecuteUpdateAsync(s => s.SetProperty(p => p.Gateway, "Mollie"));
        const string message = "Ce paiement a été fait via Mollie : configurez Payments:Gateway=Mollie sur cette application";

        Assert.Equal(message, (await Assert.ThrowsAsync<DomainValidationException>(() => h.Payments.RefundAsync(paidPayment.Id))).Message);
        Assert.Equal(message, (await Assert.ThrowsAsync<DomainValidationException>(() => h.Payments.SyncAsync(openPayment.GatewayPaymentId!))).Message);
        Assert.Equal(message, (await Assert.ThrowsAsync<DomainValidationException>(() => h.Payments.SyncByIdAsync(openPayment.Id))).Message);

        Assert.Equal(0, h.Gateway.RefundCalls);
        Assert.Equal(PaymentStatus.Paid, (await h.PaymentsOfAsync(paidPayment.ReservationId)).Single().Status);
        Assert.Equal(PaymentStatus.Open, (await h.PaymentsOfAsync(openPayment.ReservationId)).Single().Status);
    }
}
