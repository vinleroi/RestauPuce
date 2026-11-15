using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Notifications;
using ChefOS.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using static ChefOS.Tests.Infrastructure.ReservationHarness;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Les e-mails des réservations mis en file par les services (PostgreSQL, passerelle Demo, horloge figée) : un seul par événement, écrit dans la transaction
/// de l'événement, avec les gabarits définitifs.
/// </summary>
[Collection("postgres")]
public sealed class ReservationEmailOutboxTests(PostgresFixture postgres)
{
    private static string Plain(string? value) => (value ?? "").Replace(' ', ' ');

    private static async Task<string> SlugAsync(ReservationHarness h)
    {
        await using var db = h.Open();
        return await db.Restaurants.Where(r => r.Id == h.RestaurantId).Select(r => r.Slug).SingleAsync();
    }

    [Fact]
    public async Task OnlineBookingWithoutPayment_EnqueuesOneConfirmation()
    {
        var h = await CreateAsync(postgres, profile: p => p.DepositPerPerson = 0m);

        var start = await h.CreateAsync(Request(2));

        var email = Assert.Single(await h.OutboxAsync());
        Assert.Equal((EmailKind.ReservationConfirmed, "dupont@example.com", OutgoingEmailStatus.Pending), (email.Kind, email.To, email.Status));
        Assert.Equal("Votre réservation chez Bistro est confirmée", email.Subject);
        var text = Plain(email.TextBody);
        Assert.Contains("06/11/2026", text);
        Assert.Contains("19:00", text); // heure de Bruxelles (18:00 UTC)
        Assert.Contains("Dîner", text);
        Assert.Contains("2 couverts", text);
        Assert.Contains("05/11/2026 à 19:00", text); // 24 h avant, figée sur la réservation
        Assert.Contains($"https://vitrine.test/{await SlugAsync(h)}/reservation/{start.Token}", text);
        Assert.NotNull(email.HtmlBody);
    }

    [Fact]
    public async Task FullSlot_EnqueuesNothing()
    {
        var h = await CreateAsync(postgres, capacity: 2, profile: p => p.DepositPerPerson = 0m);
        await using (var db = h.Open())
        {
            db.Reservations.Add(new Reservation
            {
                PublicToken = "deja-la", StartsAtUtc = new DateTime(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc), ServiceName = "Dîner", PartySize = 2,
                CustomerName = "Déjà là", CustomerEmail = "", CustomerPhone = "0470 00 00 00", Status = ReservationStatus.Confirmed,
                CreatedAt = h.Clock.GetUtcNow().UtcDateTime, IsManual = true
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<DomainValidationException>(() => h.CreateAsync(Request(2)));

        Assert.Empty(await h.OutboxAsync());
    }

    [Fact]
    public async Task PaymentConfirmedTwice_EnqueuesOneConfirmation()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        Assert.Empty(await h.OutboxAsync()); // en attente de paiement : rien

        await h.PayAsync(start.Token);
        var gatewayId = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single().GatewayPaymentId!;
        await h.Payments.SyncAsync(gatewayId);

        var email = Assert.Single(await h.OutboxAsync());
        Assert.Equal(EmailKind.ReservationConfirmed, email.Kind);
        Assert.Contains("20,00 €", Plain(email.TextBody));
    }

    [Fact]
    public async Task LatePaymentWithSlotTaken_EnqueuesLatePaymentRefunded()
    {
        var h = await CreateAsync(postgres, capacity: 4);
        var first = await h.CreateAsync(Request(4, name: "Premier"));
        var firstPayment = (await h.PaymentsOfAsync((await h.LoadAsync(first.Token)).Id)).Single();
        h.Clock.Advance(TimeSpan.FromMinutes(31)); // la première expire, sa place est reprise
        var second = await h.CreateAsync(Request(4, name: "Second", email: "second@example.com"));
        await h.PayAsync(second.Token);

        await h.Demo.SetStatusAsync(firstPayment.GatewayPaymentId!, GatewayStatus.Paid);
        await h.Payments.SyncAsync(firstPayment.GatewayPaymentId!);

        var emails = await h.OutboxAsync();
        Assert.Equal(2, emails.Count);
        Assert.Equal((EmailKind.ReservationConfirmed, "second@example.com"), (emails[0].Kind, emails[0].To));
        var late = emails[1];
        Assert.Equal((EmailKind.LatePaymentRefunded, "dupont@example.com"), (late.Kind, late.To));
        Assert.Equal("Votre paiement à Bistro a été remboursé", late.Subject);
        Assert.Contains("40,00 €", Plain(late.TextBody));
        Assert.Contains($"https://vitrine.test/{await SlugAsync(h)}/reserver", late.TextBody);
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync((await h.LoadAsync(first.Token)).Id)).Single().Status);
    }

    [Fact]
    public async Task CustomerCancelsInTime_EnqueuesCancellationWithRefundAmount()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);

        await h.Reservations.CancelByCustomerAsync(start.Token);
        await h.Reservations.CancelByCustomerAsync(start.Token); // déjà annulée : aucun second e-mail

        var emails = await h.OutboxAsync();
        Assert.Equal([EmailKind.ReservationConfirmed, EmailKind.ReservationCancelledByCustomer], emails.Select(e => e.Kind));
        var cancelled = emails[1];
        Assert.Equal("Votre réservation chez Bistro est annulée", cancelled.Subject);
        Assert.Contains("20,00 €", Plain(cancelled.TextBody));
        Assert.DoesNotContain("sans remboursement", cancelled.TextBody);
    }

    [Fact]
    public async Task CustomerCancelsLate_EnqueuesCancellationWithoutRefund()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero)); // vendredi matin : délai gratuit dépassé

        await h.Reservations.CancelByCustomerAsync(start.Token);

        var cancelled = (await h.OutboxAsync()).Last();
        Assert.Equal(EmailKind.ReservationCancelledByCustomer, cancelled.Kind);
        Assert.Contains("sans remboursement", cancelled.TextBody);
    }

    [Fact]
    public async Task RestaurantCancelsWithRefund_EnqueuesCancelledByRestaurant()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);

        await h.Reservations.CancelByRestaurantAsync((await h.LoadAsync(start.Token)).Id, refund: true);

        var cancelled = (await h.OutboxAsync()).Last();
        Assert.Equal(EmailKind.ReservationCancelledByRestaurant, cancelled.Kind);
        Assert.Equal("Bistro a annulé votre réservation", cancelled.Subject);
        Assert.Contains("20,00 €", Plain(cancelled.TextBody));
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single().Status);
    }

    [Fact]
    public async Task ManualBookingWithEmail_EnqueuesConfirmation_WithoutEmail_EnqueuesNothing()
    {
        var h = await CreateAsync(postgres);

        await h.Reservations.CreateManualAsync(Request(2, email: ""), force: false);
        Assert.Empty(await h.OutboxAsync());

        var manual = await h.Reservations.CreateManualAsync(Request(2, name: "Au téléphone", email: "tel@example.com"), force: false);

        var email = Assert.Single(await h.OutboxAsync());
        Assert.Equal((EmailKind.ReservationConfirmed, "tel@example.com"), (email.Kind, email.To));
        await using var db = h.Open();
        var token = await db.Reservations.Where(r => r.Id == manual.ReservationId).Select(r => r.PublicToken).SingleAsync();
        Assert.Contains($"/reservation/{token}", email.TextBody);
    }

    [Fact]
    public async Task EmailsCarryTheRestaurantIdAndTheReplyTo()
    {
        var h = await CreateAsync(postgres, profile: p =>
        {
            p.DepositPerPerson = 0m;
            p.PublicEmail = "contact@bistro.be";
        });

        var start = await h.CreateAsync(Request(2));
        await h.Reservations.CancelByCustomerAsync(start.Token);

        var emails = await h.OutboxAsync();
        Assert.Equal(2, emails.Count);
        Assert.All(emails, e => Assert.Equal((h.RestaurantId, "contact@bistro.be"), (e.RestaurantId!.Value, e.ReplyTo)));
    }
}
