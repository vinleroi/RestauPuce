using ChefOS.Back.Components.Pages.Reservations;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class ReservationDayViewTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private static ReservationDetail Res(
        int id, int hourUtc = 11, int party = 2, ReservationStatus status = ReservationStatus.Confirmed, string name = "Dupont",
        IReadOnlyList<GuestDetail>? guests = null, IReadOnlyList<PaymentDetail>? payments = null, decimal due = 0m, int minute = 0, string? note = null) =>
        new(id, new DateTime(2026, 11, 7, hourUtc, minute, 0, DateTimeKind.Utc), new TimeOnly(hourUtc + 1, minute), party, name, "a@b.be", "0470 00 00 00",
            note, status, due, 0m, false, null, guests ?? [], payments ?? []);

    private static ServiceDayView Service(string name, int capacity, int booked, params ReservationDetail[] reservations) =>
        new(name, capacity, booked, reservations);

    private static ReservationDayView View(params ServiceDayView[] services) => ReservationDayView.From(services, Now, freeCancellationHours: 24);

    [Fact]
    public void Services_FollowTheirFirstReservation_EmptyOnesLast()
    {
        var view = View(
            Service("Vide", 20, 0),
            Service("Dîner", 40, 2, Res(1, 18)),
            Service("Déjeuner", 40, 2, Res(2, 11)));

        Assert.Equal(["Déjeuner", "Dîner", "Vide"], view.Services.Select(s => s.Name));
    }

    [Fact]
    public void Service_ShowsCoversOverCapacity_AndFillPercent()
    {
        var service = View(Service("Déjeuner", 40, 30)).Services[0];

        Assert.Equal("30 / 40 couverts", service.CoversText);
        Assert.Equal(75, service.FillPercent);
        Assert.False(service.IsOverCapacity);
    }

    [Fact]
    public void Service_OverBooked_CapsTheBarAtFullAndFlagsIt()
    {
        var service = View(Service("Déjeuner", 10, 14)).Services[0];

        Assert.Equal(100, service.FillPercent);
        Assert.True(service.IsOverCapacity);
    }

    [Fact]
    public void Service_WithoutCapacity_ShowsOnlyTheCovers()
    {
        var service = View(Service("Ancien", 0, 6)).Services[0];

        Assert.Equal("6 couverts", service.CoversText);
        Assert.Equal(100, service.FillPercent);
    }

    [Fact]
    public void Cards_OpenFirstByTime_ClosedAfter()
    {
        var service = View(Service("Déjeuner", 40, 6,
            Res(1, 12, status: ReservationStatus.Cancelled),
            Res(2, 12, minute: 30),
            Res(3, 11, status: ReservationStatus.Seated),
            Res(4, 12, status: ReservationStatus.NoShow, minute: 15))).Services[0];

        Assert.Equal([3, 2, 1, 4], service.Cards.Select(c => c.Id));
    }

    [Fact]
    public void Card_UsesTheLocalTimeOfTheReservation()
    {
        var card = View(Service("Déjeuner", 40, 2, Res(1, 11, minute: 30))).Services[0].Cards[0];

        Assert.Equal("12:30", card.TimeText);
    }

    [Theory]
    [InlineData(ReservationStatus.Confirmed, true, true, true)]
    [InlineData(ReservationStatus.PendingPayment, false, false, true)]
    [InlineData(ReservationStatus.Seated, false, false, false)]
    [InlineData(ReservationStatus.NoShow, false, false, false)]
    [InlineData(ReservationStatus.Cancelled, false, false, false)]
    [InlineData(ReservationStatus.Expired, false, false, false)]
    public void Card_ActionsFollowTheStatus(ReservationStatus status, bool seat, bool noShow, bool cancel)
    {
        var card = View(Service("S", 10, 2, Res(1, status: status))).Services[0].Cards[0];

        Assert.Equal(seat, card.CanSeat);
        Assert.Equal(noShow, card.CanNoShow);
        Assert.Equal(cancel, card.CanCancel);
    }

    [Fact]
    public void Card_StatusLabelsAreFrench()
    {
        Assert.Equal("Confirmée", ReservationDayView.StatusLabel(ReservationStatus.Confirmed));
        Assert.Equal("En attente de paiement", ReservationDayView.StatusLabel(ReservationStatus.PendingPayment));
        Assert.Equal("Arrivée", ReservationDayView.StatusLabel(ReservationStatus.Seated));
        Assert.Equal("No-show", ReservationDayView.StatusLabel(ReservationStatus.NoShow));
        Assert.Equal("Annulée", ReservationDayView.StatusLabel(ReservationStatus.Cancelled));
        Assert.Equal("Expirée", ReservationDayView.StatusLabel(ReservationStatus.Expired));
    }

    [Fact]
    public void Payments_ShowKindAmountAndFrenchStatus()
    {
        var payments = new[]
        {
            new PaymentDetail(7, PaymentKind.Deposit, 20m, PaymentStatus.Paid, null),
            new PaymentDetail(8, PaymentKind.Prepayment, 45.5m, PaymentStatus.Failed, null)
        };
        var card = View(Service("S", 10, 2, Res(1, payments: payments))).Services[0].Cards[0];

        Assert.Collection(card.Payments,
            p => { Assert.Equal("Acompte", p.KindLabel); Assert.Equal("Payé", p.StatusLabel); Assert.Equal("20,00 €", p.AmountText); },
            p => { Assert.Equal("Prépaiement", p.KindLabel); Assert.Equal("Échec", p.StatusLabel); Assert.Equal("45,50 €", p.AmountText); });
        Assert.Equal("Payé 20,00 €", card.PaymentSummary);
    }

    [Theory]
    [InlineData(PaymentStatus.Open, "En attente")]
    [InlineData(PaymentStatus.Paid, "Payé")]
    [InlineData(PaymentStatus.Failed, "Échec")]
    [InlineData(PaymentStatus.Expired, "Expiré")]
    [InlineData(PaymentStatus.Canceled, "Annulé")]
    [InlineData(PaymentStatus.RefundPending, "À rembourser")]
    [InlineData(PaymentStatus.Refunded, "Remboursé")]
    public void PaymentStatusLabels(PaymentStatus status, string expected) =>
        Assert.Equal(expected, ReservationDayView.PaymentStatusLabel(status));

    [Fact]
    public void Summary_PendingPaymentShowsWhatIsDue()
    {
        var card = View(Service("S", 10, 2, Res(1, status: ReservationStatus.PendingPayment, due: 20m,
            payments: [new PaymentDetail(1, PaymentKind.Deposit, 20m, PaymentStatus.Open, null)]))).Services[0].Cards[0];

        Assert.Equal("À payer 20,00 €", card.PaymentSummary);
    }

    [Fact]
    public void Summary_NoPaymentAtAll_IsNull()
    {
        var card = View(Service("S", 10, 2, Res(1))).Services[0].Cards[0];

        Assert.Null(card.PaymentSummary);
    }

    [Fact]
    public void Summary_RefundedAndPendingRefund()
    {
        var card = View(Service("S", 10, 2, Res(1, status: ReservationStatus.Cancelled, payments:
        [
            new PaymentDetail(1, PaymentKind.Deposit, 20m, PaymentStatus.Refunded, 20m),
            new PaymentDetail(2, PaymentKind.Deposit, 10m, PaymentStatus.RefundPending, 10m)
        ]))).Services[0].Cards[0];

        Assert.Equal("Remboursé 20,00 € · À rembourser 10,00 €", card.PaymentSummary);
    }

    [Fact]
    public void Refund_OnlyForCancelledOrExpired_WithPaidOrPendingRefund()
    {
        var paid = new PaymentDetail(1, PaymentKind.Deposit, 20m, PaymentStatus.Paid, null);
        var pending = new PaymentDetail(2, PaymentKind.Deposit, 20m, PaymentStatus.RefundPending, 20m);
        var refunded = new PaymentDetail(3, PaymentKind.Deposit, 20m, PaymentStatus.Refunded, 20m);

        ReservationCardView Card(ReservationStatus status, params PaymentDetail[] payments) =>
            View(Service("S", 10, 2, Res(1, status: status, payments: payments))).Services[0].Cards[0];

        Assert.Equal([1], Card(ReservationStatus.Cancelled, paid).RefundablePayments.Select(p => p.Id));
        Assert.Equal([2], Card(ReservationStatus.Expired, pending, refunded).RefundablePayments.Select(p => p.Id));
        Assert.Empty(Card(ReservationStatus.Cancelled, refunded).RefundablePayments);
        Assert.Empty(Card(ReservationStatus.Confirmed, paid).RefundablePayments);
    }

    [Fact]
    public void Cancel_OffersRefundOnlyWhenSomethingWasPaid()
    {
        var paid = new PaymentDetail(1, PaymentKind.Deposit, 20m, PaymentStatus.Paid, null);
        var open = new PaymentDetail(2, PaymentKind.Deposit, 20m, PaymentStatus.Open, null);

        var withPayment = View(Service("S", 10, 2, Res(1, payments: [paid]))).Services[0].Cards[0];
        var withoutPayment = View(Service("S", 10, 2, Res(2, payments: [open]))).Services[0].Cards[0];

        Assert.True(withPayment.CanOfferRefundOnCancel);
        Assert.Equal("Rembourser l'acompte", withPayment.RefundOnCancelLabel);
        Assert.False(withoutPayment.CanOfferRefundOnCancel);
    }

    [Fact]
    public void Cancel_RefundLabelNamesThePrepayment()
    {
        var card = View(Service("S", 10, 6, Res(1, party: 6, payments: [new PaymentDetail(1, PaymentKind.Prepayment, 90m, PaymentStatus.Paid, null)])))
            .Services[0].Cards[0];

        Assert.Equal("Rembourser le prépaiement", card.RefundOnCancelLabel);
    }

    [Fact]
    public void Cancel_RefundIsCheckedByDefault_InsideTheFreeCancellationWindow()
    {
        // Dans 3 jours : dans le délai gratuit de 24 h. À 11:00 UTC alors qu'il est 10:00 : hors délai.
        var paid = new[] { new PaymentDetail(1, PaymentKind.Deposit, 20m, PaymentStatus.Paid, null) };
        var tomorrow = new ReservationDetail(1, Now.AddDays(3), new TimeOnly(12, 0), 2, "A", "", "", null, ReservationStatus.Confirmed, 0m, 0m, false, null, [], paid);
        var soon = Res(2, payments: paid);

        var view = View(Service("S", 10, 4, tomorrow, soon));

        Assert.True(view.Services[0].Cards.Single(c => c.Id == 1).RefundOnCancelByDefault);
        Assert.False(view.Services[0].Cards.Single(c => c.Id == 2).RefundOnCancelByDefault);
    }

    [Fact]
    public void Guests_ListTheirDishes()
    {
        var guests = new[]
        {
            new GuestDetail(0, "Marie", "Dupont", [new GuestDishDetail(1, "Carbonnade", 1, 20m), new GuestDishDetail(2, "Dame blanche", 1, 8m)]),
            new GuestDetail(1, null, null, [new GuestDishDetail(1, "Carbonnade", 2, 20m)]),
            new GuestDetail(2, "Paul", null, [])
        };
        var card = View(Service("S", 10, 3, Res(1, party: 3, guests: guests))).Services[0].Cards[0];

        Assert.Equal(
            ["Marie Dupont : Carbonnade, Dame blanche", "Convive 2 : 2 × Carbonnade", "Paul : aucun plat"],
            card.Guests);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void Guests_AreCollapsedByDefault_AboveFour(int count, bool collapsed)
    {
        var guests = Enumerable.Range(0, count).Select(i => new GuestDetail(i, $"G{i}", null, [])).ToList();
        var card = View(Service("S", 10, count, Res(1, party: count, guests: guests))).Services[0].Cards[0];

        Assert.Equal(collapsed, card.GuestsCollapsedByDefault);
    }

    [Fact]
    public void NextGroup_IsTheEarliestUpcomingConfirmedLargeGroup()
    {
        var view = View(
            Service("Déjeuner", 40, 20,
                Res(1, 8, party: 8),                                          // déjà passé depuis plus d'une heure
                Res(2, 12, party: 3),                                         // trop petit
                Res(3, 13, party: 6, status: ReservationStatus.Seated),       // installé
                Res(4, 14, party: 7, name: "Martin")),
            Service("Dîner", 40, 10, Res(5, 18, party: 10)));

        var next = view.NextGroup(5);

        Assert.NotNull(next);
        Assert.Equal("Martin", next!.Name);
        Assert.Equal(7, next.PartySize);
        Assert.Equal("15:00", next.TimeText);
        Assert.Equal("Déjeuner", next.ServiceName);
    }

    [Fact]
    public void NextGroup_KeepsALateGroupStillWaiting()
    {
        var late = Res(1, 9, party: 8, minute: 30); // 09:30 UTC, il est 10:00

        Assert.Equal(1, View(Service("S", 40, 8, late)).NextGroup(5)!.Id);
    }

    [Fact]
    public void NextGroup_IsNullWithoutLargeGroup() =>
        Assert.Null(View(Service("S", 40, 4, Res(1, party: 4))).NextGroup(5));

    [Fact]
    public void TotalCovers_SumsTheServices()
    {
        Assert.Equal(50, View(Service("A", 40, 30), Service("B", 40, 20)).TotalCovers);
    }
}
