using ChefOS.Core.Common;
using ChefOS.Core.Sales;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Notifications;
using ChefOS.Infrastructure.Payments;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static ChefOS.Tests.Infrastructure.ReservationHarness;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class ReservationServiceTests(PostgresFixture postgres)
{
    private const string FullMessage = "Ce créneau vient d'être complété, choisissez-en un autre";

    private static async Task<DomainValidationException> RefusedAsync(Task task) => await Assert.ThrowsAnyAsync<DomainValidationException>(() => task);

    // ── Création ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_SmallGroup_DepositPendingThenConfirmedOnPaid()
    {
        var h = await CreateAsync(postgres);

        var start = await h.CreateAsync(Request(partySize: 4));

        Assert.Equal(ReservationStatus.PendingPayment, start.Status);
        Assert.Equal(40m, start.AmountDue);
        Assert.StartsWith("https://vitrine.test/paiement-demo/demo_", start.CheckoutUrl);
        Assert.True(start.Token.Length >= 32);
        var reservation = await h.LoadAsync(start.Token);
        var payment = Assert.Single(await h.PaymentsOfAsync(reservation.Id));
        Assert.Equal(PaymentKind.Deposit, payment.Kind);
        Assert.Equal(PaymentStatus.Open, payment.Status);
        Assert.Equal(40m, payment.Amount);
        Assert.Equal("Demo", payment.Gateway);
        Assert.Equal($"{ReturnBase}/{start.Token}", await h.Demo.GetRedirectUrlAsync(payment.GatewayPaymentId!));
        Assert.Equal(ReservationStatus.PendingPayment, (await h.Reservations.GetByTokenAsync(start.Token))!.Status);

        await h.PayAsync(start.Token);

        var confirmed = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.ConfirmedAt);
        Assert.Equal(PaymentStatus.Paid, (await h.PaymentsOfAsync(confirmed.Id)).Single().Status);
    }

    [Fact]
    public async Task GetByToken_ReadsGatewayStatusOfAnOpenPayment()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(partySize: 2));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid); // retour du client avant le webhook

        var view = await h.Reservations.GetByTokenAsync(start.Token);
        Assert.Equal(ReservationStatus.Confirmed, view!.Status);
        Assert.Null(view.CheckoutUrl);
        Assert.Equal(new TimeOnly(19, 0), view.LocalTime);
        Assert.Equal(Friday, view.LocalDate);
    }

    [Fact]
    public async Task Create_LargeGroup_RequiresGuestsAndDishes_PrepaymentOnPreorder()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak", 25m);
        var salad = await h.AddDishAsync("Salade", 15m);
        var hidden = await h.AddDishAsync("Secret", 15m, show: false);
        var archived = await h.AddDishAsync("Ancien", 15m, archived: true);
        var unpriced = await h.AddDishAsync("Sans prix", 10m);
        await using (var db = h.Open())
            await db.Recipes.Where(r => r.Id == unpriced).ExecuteUpdateAsync(s => s.SetProperty(r => r.SellingPriceInclVat, (decimal?)null));

        ReservationGuestInput[] Six(Func<int, ReservationGuestInput> guest) => Enumerable.Range(0, 6).Select(guest).ToArray();

        Assert.Equal("Indiquez les 6 convives et leurs plats", (await RefusedAsync(h.CreateAsync(Request(6)))).Message);
        Assert.Equal("Indiquez les 6 convives et leurs plats",
            (await RefusedAsync(h.CreateAsync(Request(6, guests: Six(_ => Guest("A", "B", steak)).Take(5).ToArray())))).Message);
        Assert.Equal("Chaque convive doit choisir au moins un plat",
            (await RefusedAsync(h.CreateAsync(Request(6, guests: Six(i => i == 3 ? Guest("A", "B") : Guest("A", "B", steak)))))).Message);
        foreach (var bad in new[] { hidden, archived, unpriced, 999999 })
            Assert.Equal("Un des plats choisis n'est pas disponible",
                (await RefusedAsync(h.CreateAsync(Request(6, guests: Six(i => Guest("A", "B", i == 2 ? bad : steak)))))).Message);

        var guests = Six(i => i == 0 ? Guest("Jeanne", "Martin", steak, salad) : Guest(null, null, steak));
        var start = await h.CreateAsync(Request(6, guests: guests));

        // pré-commande : 25+15 + 5×25 = 165 € TTC ; prépaiement 30 % = 49,50 €
        Assert.Equal(49.50m, start.AmountDue);
        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(165m, reservation.PreorderTotalInclVat);
        Assert.Equal(6, reservation.Guests.Count);
        Assert.Equal(PaymentKind.Prepayment, (await h.PaymentsOfAsync(reservation.Id)).Single().Kind);

        // les prix sont figés : un changement de carte n'altère pas la pré-commande
        await using (var db = h.Open())
            await db.Recipes.Where(r => r.Id == steak).ExecuteUpdateAsync(s => s.SetProperty(r => r.SellingPriceInclVat, 99m));
        var frozen = await h.LoadAsync(start.Token);
        Assert.All(frozen.Guests.SelectMany(g => g.Dishes).Where(d => d.RecipeId == steak), d => Assert.Equal(25m, d.UnitPriceInclVat));
    }

    [Fact]
    public async Task Create_SmallGroup_IgnoresGuests()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak");

        var start = await h.CreateAsync(Request(2, guests: [Guest("A", "B", steak), Guest("C", "D", 424242)]));

        Assert.Empty((await h.LoadAsync(start.Token)).Guests);
        Assert.Equal(20m, start.AmountDue);
    }

    [Fact]
    public async Task Create_ZeroAmount_ConfirmedImmediately()
    {
        var h = await CreateAsync(postgres, profile: p => p.DepositPerPerson = 0m);

        var start = await h.CreateAsync(Request(3));

        Assert.Equal(ReservationStatus.Confirmed, start.Status);
        Assert.Equal(0m, start.AmountDue);
        Assert.Null(start.CheckoutUrl);
        var reservation = await h.LoadAsync(start.Token);
        Assert.NotNull(reservation.ConfirmedAt);
        Assert.Empty(await h.PaymentsOfAsync(reservation.Id));
        var email = Assert.Single(await h.OutboxAsync());
        Assert.Equal(("dupont@example.com", EmailKind.ReservationConfirmed, h.RestaurantId), (email.To, email.Kind, email.RestaurantId));
        Assert.Equal(OutgoingEmailStatus.Pending, email.Status);
    }

    [Fact]
    public async Task Create_Concurrent_LastSeats_NoOverbooking()
    {
        var h = await CreateAsync(postgres, capacity: 10, profile: p => p.GroupThreshold = 10);
        await h.CreateAsync(Request(partySize: 6, name: "Déjà là")); // 4 places restantes
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            // un service (donc des contextes) par tâche
            var (_, service) = Services(h.Options, h.RestaurantId, h.Clock, h.Gateway);
            try
            {
                await service.CreateAsync(Request(partySize: 1, name: $"Client {i}", email: $"c{i}@example.com"), ReturnBase, Webhook);
                return ((string?)null, true);
            }
            catch (DomainValidationException ex)
            {
                return ((string?)ex.Message, false);
            }
        }));

        Assert.Equal(4, results.Count(r => r.Item2));
        Assert.All(results.Where(r => !r.Item2), r => Assert.Equal(FullMessage, r.Item1));
        await using var db = h.Open();
        Assert.Equal(10, await db.Reservations.SumAsync(r => r.PartySize));
        Assert.Empty(await h.Reservations.GetSlotsAsync(Friday, 1));
    }

    [Fact]
    public async Task Create_Concurrent_LargeAndSmallParties_NeverExceedCapacity()
    {
        var h = await CreateAsync(postgres, capacity: 12);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var (_, service) = Services(h.Options, h.RestaurantId, h.Clock, h.Gateway);
            try
            {
                await service.CreateAsync(Request(partySize: 2 + i % 3), ReturnBase, Webhook);
                return true;
            }
            catch (DomainValidationException)
            {
                return false;
            }
        }));

        await using var db = h.Open();
        Assert.InRange(await db.Reservations.SumAsync(r => r.PartySize), 1, 12);
        Assert.Contains(results, ok => !ok);
    }

    [Fact]
    public void LockKey_IsStable_AndDependsOnServiceAndDate()
    {
        var a = ReservationCapacity.LockKey("Dîner", new DateOnly(2026, 11, 6));
        Assert.Equal(a, ReservationCapacity.LockKey("Dîner", new DateOnly(2026, 11, 6)));
        Assert.NotEqual(a, ReservationCapacity.LockKey("Dîner", new DateOnly(2026, 11, 7)));
        Assert.NotEqual(a, ReservationCapacity.LockKey("Déjeuner", new DateOnly(2026, 11, 6)));
        // SHA-256("Dîner|2026-11-06"), 4 premiers octets en big-endian : valeur stable d'un processus à l'autre
        var expected = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Dîner|2026-11-06")));
        Assert.Equal(expected, a);
    }

    [Fact]
    public async Task Create_UnpublishedRestaurant_Refused()
    {
        var unpublished = await CreateAsync(postgres, profile: p => p.IsPublished = false);
        var closed = await CreateAsync(postgres, profile: p => p.ReservationsOpen = false);

        Assert.Equal("Les réservations en ligne sont fermées", (await RefusedAsync(unpublished.CreateAsync(Request()))).Message);
        Assert.Equal("Les réservations en ligne sont fermées", (await RefusedAsync(closed.CreateAsync(Request()))).Message);
        Assert.Empty(await unpublished.Reservations.GetSlotsAsync(Friday, 2));
        Assert.Empty(await closed.Reservations.GetSlotsAsync(Friday, 2));
    }

    [Fact]
    public async Task Create_ClosedDayOrPastSlot_Refused()
    {
        var h = await CreateAsync(postgres);
        await using (var db = h.Open())
        {
            db.ClosureDays.Add(new ClosureDay { DateLocal = new DateOnly(2026, 11, 13), Reason = "Congé" });
            await db.SaveChangesAsync();
        }

        Assert.Equal("Le restaurant est fermé ce jour-là", (await RefusedAsync(h.CreateAsync(Request(date: new DateOnly(2026, 11, 13))))).Message);
        Assert.Equal("Ce créneau n'existe pas", (await RefusedAsync(h.CreateAsync(Request(date: new DateOnly(2026, 11, 7))))).Message); // samedi : pas de service
        Assert.Equal("Ce créneau n'existe pas", (await RefusedAsync(h.CreateAsync(Request(time: new TimeOnly(19, 10))))).Message);
        Assert.Equal("Ce créneau n'existe pas", (await RefusedAsync(h.CreateAsync(Request(time: new TimeOnly(22, 0))))).Message);
        Assert.Contains("trop éloigné", (await RefusedAsync(h.CreateAsync(Request(date: new DateOnly(2027, 2, 12))))).Message);

        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero)); // 18:00 locale
        Assert.Equal("Ce créneau est déjà passé", (await RefusedAsync(h.CreateAsync(Request(time: new TimeOnly(18, 0))))).Message);
        Assert.Contains("trop proche", (await RefusedAsync(h.CreateAsync(Request(time: new TimeOnly(19, 30))))).Message);
        Assert.Empty(await h.Reservations.GetSlotsAsync(new DateOnly(2026, 11, 13), 2));

        await using var verify = h.Open();
        Assert.Equal(0, await verify.Reservations.CountAsync());
    }

    [Theory]
    [InlineData(0, "Dupont", "a@b.be", "0470123456", "Le nombre de couverts est invalide")]
    [InlineData(21, "Dupont", "a@b.be", "0470123456", "Le nombre de couverts est limité à 20")]
    [InlineData(2, "  ", "a@b.be", "0470123456", "Le nom est obligatoire")]
    [InlineData(2, "Dupont", "pas-un-mail", "0470123456", "L'adresse e-mail est invalide")]
    [InlineData(2, "Dupont", "a@b", "0470123456", "L'adresse e-mail est invalide")]
    [InlineData(2, "Dupont", "a@b.be", " 12 34 5 ", "Le numéro de téléphone est invalide (6 caractères minimum)")]
    public async Task Create_InvalidContactOrParty_RefusedInFrench(int party, string name, string email, string phone, string message)
    {
        var h = await CreateAsync(postgres);

        Assert.Equal(message, (await RefusedAsync(h.CreateAsync(Request(party, name: name, email: email, phone: phone)))).Message);
    }

    [Fact]
    public async Task Create_TrimsContactFields()
    {
        var h = await CreateAsync(postgres);

        var start = await h.CreateAsync(Request(name: "  Dupont  ", email: " d@x.be ", phone: " 123456 "));

        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(("Dupont", "d@x.be", "123456"), (reservation.CustomerName, reservation.CustomerEmail, reservation.CustomerPhone));
    }

    [Fact]
    public async Task Create_GatewayFailure_ExpiresReservationAndFreesSeats()
    {
        var h = await CreateAsync(postgres, capacity: 4);
        h.Gateway.FailCreate = true;

        var ex = await RefusedAsync(h.CreateAsync(Request(4)));

        Assert.Equal("Le paiement n'a pas pu être initialisé, réessayez", ex.Message);
        await using var db = h.Open();
        var reservation = await db.Reservations.SingleAsync();
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.Equal(PaymentStatus.Failed, (await h.PaymentsOfAsync(reservation.Id)).Single().Status);
        Assert.Single(await h.Reservations.GetSlotsAsync(Friday, 4), s => s.Time == Seven);
    }

    // ── Paiement ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(GatewayStatus.Failed, PaymentStatus.Failed)]
    [InlineData(GatewayStatus.Expired, PaymentStatus.Expired)]
    [InlineData(GatewayStatus.Canceled, PaymentStatus.Canceled)]
    public async Task Payment_FailedOrExpired_ReleasesSeat(GatewayStatus gatewayStatus, PaymentStatus expected)
    {
        var h = await CreateAsync(postgres, capacity: 4);
        var start = await h.CreateAsync(Request(4));
        Assert.Empty(await h.Reservations.GetSlotsAsync(Friday, 1));
        var payment = (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single();

        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, gatewayStatus);
        Assert.Equal(expected, await h.Payments.SyncAsync(payment.GatewayPaymentId!));

        Assert.Equal(ReservationStatus.Expired, (await h.LoadAsync(start.Token)).Status);
        Assert.NotEmpty(await h.Reservations.GetSlotsAsync(Friday, 4));
    }

    [Fact]
    public async Task Create_PendingOlderThanTimeout_ExpiresAndFreesSeats()
    {
        var h = await CreateAsync(postgres, capacity: 4);
        var start = await h.CreateAsync(Request(4));
        Assert.Empty(await h.Reservations.GetSlotsAsync(Friday, 1));

        h.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.NotEmpty(await h.Reservations.GetSlotsAsync(Friday, 4));
        Assert.Equal(ReservationStatus.Expired, (await h.LoadAsync(start.Token)).Status);
        var again = await h.CreateAsync(Request(4, name: "Autre", email: "autre@example.com"));
        Assert.Equal(ReservationStatus.PendingPayment, again.Status);
    }

    // ── Annulation par le client ─────────────────────────────────────────────

    [Fact]
    public async Task Cancel_FreeWindow_Refunded_LateNoRefund()
    {
        var h = await CreateAsync(postgres);
        var early = await h.CreateAsync(Request(2, name: "Tôt"));
        var late = await h.CreateAsync(Request(2, name: "Tard", email: "tard@example.com"));
        await h.PayAsync(early.Token);
        await h.PayAsync(late.Token);
        var earlyPayment = (await h.PaymentsOfAsync((await h.LoadAsync(early.Token)).Id)).Single();
        var latePayment = (await h.PaymentsOfAsync((await h.LoadAsync(late.Token)).Id)).Single();

        await h.Reservations.CancelByCustomerAsync(early.Token); // lundi : bien avant 24 h

        var cancelled = await h.LoadAsync(early.Token);
        Assert.Equal(ReservationStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        var refunded = (await h.PaymentsOfAsync(cancelled.Id)).Single();
        Assert.Equal(PaymentStatus.Refunded, refunded.Status);
        Assert.Equal(20m, refunded.RefundAmount);
        Assert.Equal(20m, await h.Demo.RefundedAmountAsync(earlyPayment.GatewayPaymentId!));

        // jeudi 18:30 UTC : 23 h 30 avant le repas de 18:00 UTC le vendredi
        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 18, 30, 0, TimeSpan.Zero));
        await h.Reservations.CancelByCustomerAsync(late.Token);

        var lateCancelled = await h.LoadAsync(late.Token);
        Assert.Equal(ReservationStatus.Cancelled, lateCancelled.Status);
        Assert.Equal(PaymentStatus.Paid, (await h.PaymentsOfAsync(lateCancelled.Id)).Single().Status);
        Assert.Equal(0m, await h.Demo.RefundedAmountAsync(latePayment.GatewayPaymentId!));

        await h.Reservations.CancelByCustomerAsync(early.Token); // déjà annulée : sans effet
        Assert.Equal(1, h.Gateway.RefundCalls);
    }

    [Fact]
    public async Task Cancel_BoundaryOf24Hours_IsStillFree()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);

        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero)); // exactement 24 h avant
        await h.Reservations.CancelByCustomerAsync(start.Token);

        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync((await h.LoadAsync(start.Token)).Id)).Single().Status);
    }

    [Fact]
    public async Task Cancel_GatewayRefundFails_LeavesRefundPendingThenBackRetries()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        h.Gateway.FailRefund = true;

        await h.Reservations.CancelByCustomerAsync(start.Token);

        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        var payment = (await h.PaymentsOfAsync(reservation.Id)).Single();
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);

        h.Gateway.FailRefund = false;
        await h.Payments.RefundAsync(payment.Id);
        await h.Payments.RefundAsync(payment.Id); // idempotent

        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(reservation.Id)).Single().Status);
        Assert.Equal(20m, await h.Demo.RefundedAmountAsync(payment.GatewayPaymentId!));
    }

    [Fact]
    public async Task Cancel_SeatedNoShowOrPast_Refused()
    {
        var h = await CreateAsync(postgres, profile: p => p.DepositPerPerson = 0m);
        var seated = await h.CreateAsync(Request(2, name: "Installé"));
        var noShow = await h.CreateAsync(Request(2, name: "Absent", email: "abs@example.com"));
        var past = await h.CreateAsync(Request(2, name: "Passé", email: "past@example.com"));
        await h.Reservations.SeatAsync((await h.LoadAsync(seated.Token)).Id, ReservationStatus.Confirmed);
        await h.Reservations.MarkNoShowAsync((await h.LoadAsync(noShow.Token)).Id, ReservationStatus.Confirmed);

        Assert.Equal("Cette réservation ne peut plus être annulée", (await RefusedAsync(h.Reservations.CancelByCustomerAsync(seated.Token))).Message);
        Assert.Equal("Cette réservation ne peut plus être annulée", (await RefusedAsync(h.Reservations.CancelByCustomerAsync(noShow.Token))).Message);
        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 18, 30, 0, TimeSpan.Zero)); // le repas a commencé
        Assert.Equal("Cette réservation ne peut plus être annulée", (await RefusedAsync(h.Reservations.CancelByCustomerAsync(past.Token))).Message);
        Assert.Equal(ReservationStatus.Seated, (await h.LoadAsync(seated.Token)).Status);
        await RefusedAsync(h.Reservations.CancelByCustomerAsync("inconnu"));
    }

    [Fact]
    public async Task CancelByRestaurant_RefundsWhenAsked()
    {
        var h = await CreateAsync(postgres);
        var refunded = await h.CreateAsync(Request(2, name: "A"));
        var kept = await h.CreateAsync(Request(2, name: "B", email: "b@example.com"));
        await h.PayAsync(refunded.Token);
        await h.PayAsync(kept.Token);

        await h.Reservations.CancelByRestaurantAsync((await h.LoadAsync(refunded.Token)).Id, refund: true);
        await h.Reservations.CancelByRestaurantAsync((await h.LoadAsync(kept.Token)).Id, refund: false);

        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync((await h.LoadAsync(refunded.Token)).Id)).Single().Status);
        Assert.Equal(PaymentStatus.Paid, (await h.PaymentsOfAsync((await h.LoadAsync(kept.Token)).Id)).Single().Status);
        await RefusedAsync(h.Reservations.CancelByRestaurantAsync((await h.LoadAsync(kept.Token)).Id, refund: true)); // déjà annulée
    }

    // ── Arrivée en salle ─────────────────────────────────────────────────────

    [Fact]
    public async Task Seat_CreatesOrderWithGuestNames()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak", 25m, 0.12m);
        var salad = await h.AddDishAsync("Salade", 15m, 0.06m);
        var start = await h.CreateAsync(Request(6, note: "Anniversaire", guests:
        [
            Guest("Jeanne", "Martin", steak, salad), Guest("Paul", null, steak), Guest(null, null, salad),
            Guest(null, "Durand", steak), Guest("E", "F", steak), Guest("G", "H", steak)
        ]));
        await h.PayAsync(start.Token);
        var id = (await h.LoadAsync(start.Token)).Id;

        await h.Reservations.SeatAsync(id, ReservationStatus.Confirmed);

        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Seated, reservation.Status);
        Assert.NotNull(reservation.OrderId);
        await using var db = h.Open();
        var order = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == reservation.OrderId);
        Assert.Equal(OrderSource.Vitrine, order.Source);
        Assert.Equal(OrderStatus.Nouvelle, order.Status);
        Assert.Equal("Dupont", order.TableOrCustomer);
        Assert.Equal("Réservation Dupont · 6 couverts · Anniversaire · Déjà réglé en ligne : 46,50 € (prépaiement)", order.Note);
        Assert.Equal(7, order.Lines.Count);
        var notes = order.Lines.OrderBy(l => l.Id).Select(l => (l.RecipeId, l.Note)).ToList();
        Assert.Equal([(steak, "Jeanne Martin"), (salad, "Jeanne Martin"), (steak, "Paul"), (salad, "Convive 3"), (steak, "Durand"), (steak, "E F"), (steak, "G H")], notes);
        Assert.Contains(order.Lines, l => l.RecipeId == salad && l.UnitPriceInclVat == 15m && l.VatRate == 0.06m);
    }

    [Fact]
    public async Task Seat_ArchivedDishAfterBooking_StillCreatesOrderFromFrozenLines()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak", 25m);
        var start = await h.CreateAsync(Request(6, guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));
        await h.PayAsync(start.Token);
        await using (var db = h.Open())
            await db.Recipes.Where(r => r.Id == steak).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsArchived, true).SetProperty(r => r.SellingPriceInclVat, (decimal?)null));

        await h.Reservations.SeatAsync((await h.LoadAsync(start.Token)).Id, ReservationStatus.Confirmed);

        await using var verify = h.Open();
        var order = await verify.Orders.Include(o => o.Lines).SingleAsync();
        Assert.Equal(6, order.Lines.Count);
        Assert.All(order.Lines, l => Assert.Equal(25m, l.UnitPriceInclVat));
    }

    [Fact]
    public async Task Seat_SmallGroupWithoutPreorder_CreatesNoOrder()
    {
        var h = await CreateAsync(postgres, profile: p => p.DepositPerPerson = 0m);
        var start = await h.CreateAsync(Request(2));

        await h.Reservations.SeatAsync((await h.LoadAsync(start.Token)).Id, ReservationStatus.Confirmed);

        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Seated, reservation.Status);
        Assert.Null(reservation.OrderId);
        await using var db = h.Open();
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Seat_StaleStatus_Refused()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak");
        var start = await h.CreateAsync(Request(6, guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));
        var id = (await h.LoadAsync(start.Token)).Id;

        // encore en attente de paiement : on ne peut pas installer
        var pending = await RefusedAsync(h.Reservations.SeatAsync(id, ReservationStatus.Confirmed));
        Assert.Equal("La réservation a déjà changé de statut", pending.Message);
        await RefusedAsync(h.Reservations.SeatAsync(id, ReservationStatus.PendingPayment));

        await h.PayAsync(start.Token);
        await h.Reservations.MarkNoShowAsync(id, ReservationStatus.Confirmed);
        await RefusedAsync(h.Reservations.SeatAsync(id, ReservationStatus.Confirmed)); // écran périmé

        await using var db = h.Open();
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(ReservationStatus.NoShow, (await h.LoadAsync(start.Token)).Status);
        await RefusedAsync(h.Reservations.MarkNoShowAsync(id, ReservationStatus.Confirmed));
        await RefusedAsync(h.Reservations.SeatAsync(424242, ReservationStatus.Confirmed));
    }

    [Fact]
    public async Task Seat_Concurrent_CreatesOneOrderOnly()
    {
        var h = await CreateAsync(postgres, profile: p => p.PrepaymentPercent = 0m);
        var steak = await h.AddDishAsync("Steak");
        var start = await h.CreateAsync(Request(6, guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));
        var id = (await h.LoadAsync(start.Token)).Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var (_, service) = Services(h.Options, h.RestaurantId, h.Clock, h.Gateway);
            try
            {
                await service.SeatAsync(id, ReservationStatus.Confirmed);
                return true;
            }
            catch (DomainValidationException)
            {
                return false;
            }
        }));

        Assert.Equal(1, results.Count(r => r));
        await using var db = h.Open();
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    // ── Réservation manuelle, liste, isolation ───────────────────────────────

    [Fact]
    public async Task Manual_OverCapacityNeedsForce()
    {
        var h = await CreateAsync(postgres, capacity: 6);
        await h.CreateAsync(Request(4));

        var refused = await RefusedAsync(h.Reservations.CreateManualAsync(Request(4, name: "Téléphone", email: "", phone: ""), force: false));
        Assert.Contains("Capacité dépassée", refused.Message);
        Assert.Contains("2 couvert(s) restant(s)", refused.Message);
        var confirmation = Assert.IsType<ConfirmationRequiredException>(refused);
        Assert.Equal("Capacité dépassée : 2 couvert(s) restant(s) pour 4 demandé(s)", confirmation.Prompt);

        var ok = await h.Reservations.CreateManualAsync(Request(2, name: "Dans les clous", email: "", phone: ""), force: false);
        Assert.Null(ok.Warning);
        var forced = await h.Reservations.CreateManualAsync(Request(3, name: "Forcé", email: "", phone: ""), force: true);
        Assert.Contains("Capacité dépassée", forced.Warning);

        await using var db = h.Open();
        var manual = await db.Reservations.SingleAsync(r => r.Id == forced.ReservationId);
        Assert.True(manual.IsManual);
        Assert.Equal(ReservationStatus.Confirmed, manual.Status);
        Assert.Equal(0m, manual.AmountDue);
        Assert.Empty(await db.Payments.Where(p => p.ReservationId == forced.ReservationId).ToListAsync());
    }

    [Fact]
    public async Task Manual_RequiresNameAndAValidSlot_ClosedDayNeedsForce()
    {
        var h = await CreateAsync(postgres);
        await using (var db = h.Open())
        {
            db.ClosureDays.Add(new ClosureDay { DateLocal = new DateOnly(2026, 11, 13) });
            await db.SaveChangesAsync();
        }

        Assert.Equal("Le nom est obligatoire", (await RefusedAsync(h.Reservations.CreateManualAsync(Request(name: " "), false))).Message);
        Assert.Equal("Ce créneau n'existe pas", (await RefusedAsync(h.Reservations.CreateManualAsync(Request(time: new TimeOnly(15, 0)), true))).Message);
        var closed = await RefusedAsync(h.Reservations.CreateManualAsync(Request(date: new DateOnly(2026, 11, 13)), false));
        Assert.Equal("Le restaurant est fermé ce jour-là", Assert.IsType<ConfirmationRequiredException>(closed).Prompt);
        Assert.IsNotType<ConfirmationRequiredException>(await RefusedAsync(h.Reservations.CreateManualAsync(Request(name: " "), false)));
        var forced = await h.Reservations.CreateManualAsync(Request(date: new DateOnly(2026, 11, 13)), true);
        Assert.Equal("Le restaurant est fermé ce jour-là", forced.Warning);
    }

    [Fact]
    public async Task ListForDay_GroupsByServiceWithGuestsDishesAndPayments()
    {
        var h = await CreateAsync(postgres, capacity: 20);
        var steak = await h.AddDishAsync("Steak", 25m);
        var big = await h.CreateAsync(Request(6, name: "Groupe", guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));
        await h.PayAsync(big.Token);
        var pending = await h.CreateAsync(Request(2, name: "Attente", email: "w@example.com", time: new TimeOnly(20, 0)));
        var failed = await h.CreateAsync(Request(3, name: "Raté", email: "f@example.com"));
        var failedPayment = (await h.PaymentsOfAsync((await h.LoadAsync(failed.Token)).Id)).Single();
        await h.Demo.SetStatusAsync(failedPayment.GatewayPaymentId!, GatewayStatus.Failed);
        await h.Payments.SyncAsync(failedPayment.GatewayPaymentId!);

        var days = await h.Reservations.ListForDayAsync(Friday);

        var service = Assert.Single(days);
        Assert.Equal("Dîner", service.ServiceName);
        Assert.Equal(20, service.Capacity);
        Assert.Equal(8, service.BookedCovers); // 6 confirmés + 2 en attente ; les 3 « Raté » sont expirés
        Assert.Equal(3, service.Reservations.Count);
        var group = service.Reservations.Single(r => r.CustomerName == "Groupe");
        Assert.Equal(6, group.Guests.Count);
        Assert.Equal("Steak", group.Guests[0].Dishes[0].DishName);
        Assert.Equal(PaymentStatus.Paid, group.Payments.Single().Status);
        Assert.Equal(new TimeOnly(20, 0), service.Reservations.Single(r => r.CustomerName == "Attente").LocalTime);
        Assert.Equal(ReservationStatus.Expired, service.Reservations.Single(r => r.CustomerName == "Raté").Status);
        Assert.Empty(await h.Reservations.ListForDayAsync(new DateOnly(2026, 11, 7)));
        Assert.NotNull(pending);
    }

    [Fact]
    public async Task GetByToken_UnknownToken_Null()
    {
        var h = await CreateAsync(postgres);

        Assert.Null(await h.Reservations.GetByTokenAsync("n'existe-pas"));
        Assert.Null(await h.Reservations.GetByTokenAsync(""));
        Assert.Null(await h.Reservations.GetByTokenAsync(new string('x', 200)));
    }

    [Fact]
    public async Task OtherRestaurant_Isolated()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak");
        var start = await h.CreateAsync(Request(2));
        var id = (await h.LoadAsync(start.Token)).Id;
        var otherId = await TestData.CreateRestaurantAsync(h.Options, "Autre");
        await using (var db = h.Open(otherId))
        {
            db.VitrineProfiles.Add(new VitrineProfile { IsPublished = true, ReservationsOpen = true });
            await db.SaveChangesAsync();
        }

        var (otherPayments, other) = Services(h.Options, otherId, h.Clock, h.Gateway);

        Assert.Null(await other.GetByTokenAsync(start.Token));
        await RefusedAsync(other.CancelByCustomerAsync(start.Token));
        await RefusedAsync(other.SeatAsync(id, ReservationStatus.Confirmed));
        await RefusedAsync(other.MarkNoShowAsync(id, ReservationStatus.Confirmed));
        await RefusedAsync(other.CancelByRestaurantAsync(id, true));
        Assert.Empty(await other.ListForDayAsync(Friday));
        var payment = (await h.PaymentsOfAsync(id)).Single();
        await h.Demo.SetStatusAsync(payment.GatewayPaymentId!, GatewayStatus.Paid);
        Assert.Null(await otherPayments.SyncAsync(payment.GatewayPaymentId!));
        Assert.Equal(ReservationStatus.PendingPayment, (await h.LoadAsync(start.Token)).Status);
        Assert.NotEqual(0, steak);
    }

    [Fact]
    public async Task Cancel_ClaimsRefundInTheSameTransaction_GatewayCalledAfterCommit()
    {
        var h = await CreateAsync(postgres);
        var start = await h.CreateAsync(Request(2));
        await h.PayAsync(start.Token);
        var release = new TaskCompletionSource();
        h.Gateway.RefundBlock = release.Task;

        var cancel = Task.Run(() => h.Reservations.CancelByCustomerAsync(start.Token));
        while (h.Gateway.RefundCalls == 0)
            await Task.Delay(20);

        // la passerelle est en cours d'appel : réservation annulée et paiement RefundPending sont déjà validés ensemble
        var reservation = await h.LoadAsync(start.Token);
        Assert.Equal(ReservationStatus.Cancelled, reservation.Status);
        Assert.Equal(PaymentStatus.RefundPending, (await h.PaymentsOfAsync(reservation.Id)).Single().Status);
        release.SetResult();
        await cancel;
        Assert.Equal(PaymentStatus.Refunded, (await h.PaymentsOfAsync(reservation.Id)).Single().Status);
    }

    [Fact]
    public async Task Seat_OrderTimestampsUseTheInjectedClock()
    {
        var h = await CreateAsync(postgres, profile: p => p.PrepaymentPercent = 0m);
        var steak = await h.AddDishAsync("Steak");
        var start = await h.CreateAsync(Request(6, guests: Enumerable.Range(0, 6).Select(i => Guest($"G{i}", "X", steak)).ToList()));
        h.Clock.Advance(TimeSpan.FromHours(3));

        await h.Reservations.SeatAsync((await h.LoadAsync(start.Token)).Id, ReservationStatus.Confirmed);

        await using var db = h.Open();
        var order = await db.Orders.SingleAsync();
        var expected = h.Clock.GetUtcNow().UtcDateTime;
        Assert.Equal(expected, order.CreatedAt);
        Assert.Equal(expected, order.StatusChangedAt);
    }

    [Fact]
    public async Task Create_InputCaps_RefusedInFrench()
    {
        var h = await CreateAsync(postgres);
        var steak = await h.AddDishAsync("Steak");
        string Long(int n) => new('x', n);

        Assert.Equal("Le nom ne peut pas dépasser 120 caractères", (await RefusedAsync(h.CreateAsync(Request(name: Long(121))))).Message);
        Assert.Equal("L'adresse e-mail ne peut pas dépasser 200 caractères", (await RefusedAsync(h.CreateAsync(Request(email: Long(196) + "@b.be")))).Message);
        Assert.Equal("Le numéro de téléphone ne peut pas dépasser 40 caractères", (await RefusedAsync(h.CreateAsync(Request(phone: Long(41))))).Message);
        Assert.Equal("La note ne peut pas dépasser 1000 caractères", (await RefusedAsync(h.CreateAsync(Request(note: Long(1001))))).Message);
        var tooMany = Enumerable.Range(0, 6).Select(i => Guest("A", "B", Enumerable.Repeat(steak, i == 0 ? 11 : 1).ToArray())).ToList();
        Assert.Equal("Un convive ne peut pas choisir plus de 10 plats", (await RefusedAsync(h.CreateAsync(Request(6, guests: tooMany)))).Message);

        var ok = await h.CreateAsync(Request(name: Long(120), note: Long(1000), phone: Long(40), email: Long(194) + "@b.be"));
        Assert.Equal(ReservationStatus.PendingPayment, ok.Status);
        await RefusedAsync(h.Reservations.CreateManualAsync(Request(note: Long(1001)), true));
    }

    // ── Corrections : synchronisation bornée, créneaux manuels ───────────────

    private static async Task<List<Payment>> OpenDepositsAsync(ReservationHarness h, int count)
    {
        for (var i = 0; i < count; i++)
            await h.CreateAsync(Request(2, name: $"Attente {i}", email: $"w{i}@example.com"));
        await using var db = h.Open();
        return await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Open).OrderBy(p => p.Id).ToListAsync();
    }

    [Fact]
    public async Task ListForDay_StopsSyncingAfterFourSeconds()
    {
        var h = await CreateAsync(postgres, capacity: 20);
        await OpenDepositsAsync(h, 3);
        h.Gateway.StatusDelay = TimeSpan.FromSeconds(3);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var services = await h.Reservations.ListForDayAsync(Friday);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"ListForDayAsync a duré {watch.Elapsed}");
        Assert.Equal(3, Assert.Single(services).Reservations.Count);
        Assert.True(h.Gateway.StatusCalls.Count < 3);
    }

    [Fact]
    public async Task ListForDay_SyncsNewestPaymentsFirst()
    {
        var h = await CreateAsync(postgres, capacity: 20);
        var open = await OpenDepositsAsync(h, 3);

        await h.Reservations.ListForDayAsync(Friday);

        Assert.Equal(open.OrderByDescending(p => p.Id).Select(p => p.GatewayPaymentId), h.Gateway.StatusCalls);
    }

    [Fact]
    public async Task ManualSlots_FollowTheServiceGrid()
    {
        var h = await CreateAsync(postgres, capacity: 1);
        await h.CreateAsync(Request(1)); // le service est plein : la grille ne tient compte ni de la capacité ni du délai

        var slots = await h.Reservations.GetManualSlotsAsync(Friday, "Dîner");

        Assert.Equal(Enumerable.Range(0, 8).Select(i => new TimeOnly(18, 0).AddMinutes(30 * i)), slots);
        Assert.Empty(await h.Reservations.GetManualSlotsAsync(Friday, "Inconnu"));
        Assert.Empty(await h.Reservations.GetManualSlotsAsync(Friday.AddDays(1), "Dîner"));
    }

    [Fact]
    public async Task ManualSlots_EveningServiceStopsBeforeItsEnd()
    {
        var h = await CreateAsync(postgres);
        await using (var db = h.Open())
        {
            db.ServiceSchedules.Add(new ServiceSchedule
            {
                DayOfWeek = DayOfWeek.Friday, Name = "Soir", StartLocal = new TimeOnly(19, 0), EndLocal = new TimeOnly(22, 0),
                Capacity = 10, SlotMinutes = 30
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(Enumerable.Range(0, 6).Select(i => new TimeOnly(19, 0).AddMinutes(30 * i)), await h.Reservations.GetManualSlotsAsync(Friday, "Soir"));
    }
}
