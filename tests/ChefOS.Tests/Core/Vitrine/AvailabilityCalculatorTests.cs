using ChefOS.Core.Vitrine;

namespace ChefOS.Tests.Core.Vitrine;

public class AvailabilityCalculatorTests
{
    private const string Tz = "Europe/Brussels";
    private static readonly DateOnly Friday = new(2026, 11, 6);
    // Lundi 02/11/2026 10:00 UTC : largement avant le vendredi.
    private static readonly DateTime Now = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

    private static ServiceSchedule Dinner(int capacity = 20) => new()
    {
        DayOfWeek = DayOfWeek.Friday, Name = "Dîner",
        StartLocal = new TimeOnly(19, 0), EndLocal = new TimeOnly(21, 0),
        Capacity = capacity, SlotMinutes = 30,
    };

    private static (DateTime StartsAtUtc, string ServiceName, int PartySize, ReservationStatus Status, DateTime CreatedAt)
        Res(int party, ReservationStatus status, DateTime? createdAt = null, string service = "Dîner") =>
        (RestaurantClock.ToUtc(Friday, new TimeOnly(19, 30), Tz), service, party, status, createdAt ?? Now);

    private static IReadOnlyList<Slot> Slots(
        IReadOnlyList<ServiceSchedule>? schedules = null,
        IReadOnlyList<ClosureDay>? closures = null,
        IReadOnlyList<(DateTime, string, int, ReservationStatus, DateTime)>? reservations = null,
        VitrineProfile? rules = null, DateOnly? date = null, DateTime? now = null, int party = 2) =>
        AvailabilityCalculator.SlotsFor(date ?? Friday, schedules ?? new[] { Dinner() }, closures ?? Array.Empty<ClosureDay>(),
            reservations ?? Array.Empty<(DateTime, string, int, ReservationStatus, DateTime)>(),
            rules ?? new VitrineProfile(), Tz, now ?? Now, party);

    [Fact]
    public void Availability_SlotsAndRemaining()
    {
        var slots = Slots(reservations: new[] { Res(4, ReservationStatus.Confirmed), Res(2, ReservationStatus.Seated) });

        Assert.Equal(new[] { new TimeOnly(19, 0), new TimeOnly(19, 30), new TimeOnly(20, 0), new TimeOnly(20, 30) },
            slots.Select(s => s.Time).ToArray());
        Assert.All(slots, s => Assert.Equal(14, s.RemainingCovers));
        Assert.All(slots, s => Assert.Equal("Dîner", s.ServiceName));
        Assert.All(slots, s => Assert.Equal(DateTimeKind.Utc, s.StartsAtUtc.Kind));
        Assert.Equal(new DateTime(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc), slots[0].StartsAtUtc);
    }

    [Fact]
    public void Availability_ClosedDayAndNoService_Empty()
    {
        var closed = Slots(closures: new[] { new ClosureDay { DateLocal = Friday } });
        Assert.Empty(closed);

        var saturday = Slots(date: new DateOnly(2026, 11, 7));
        Assert.Empty(saturday);
    }

    [Fact]
    public void Availability_LeadTimeAndMaxDays()
    {
        var rules = new VitrineProfile { MinLeadMinutes = 120 };

        // now = 16:30 UTC → +2h = 18:30 UTC = 19:30 locale : le créneau de 19:30 (borne) est autorisé, pas 19:00.
        var now = new DateTime(2026, 11, 6, 16, 30, 0, DateTimeKind.Utc);
        var slots = Slots(rules: rules, now: now);
        Assert.Equal(new TimeOnly(19, 30), slots[0].Time);
        Assert.Equal(3, slots.Count);

        // MaxDaysAhead : aujourd'hui local = 02/11 ; +4 jours = 06/11 autorisé, 05 jours non.
        var narrow = new VitrineProfile { MaxDaysAhead = 4 };
        Assert.NotEmpty(Slots(rules: narrow));
        var tight = new VitrineProfile { MaxDaysAhead = 3 };
        Assert.Empty(Slots(rules: tight));
    }

    [Fact]
    public void Availability_IgnoresExpiredPending()
    {
        var rules = new VitrineProfile { PendingPaymentMinutes = 30 };
        var fresh = Res(10, ReservationStatus.PendingPayment, Now.AddMinutes(-10));
        var expired = Res(10, ReservationStatus.PendingPayment, Now.AddMinutes(-31));

        Assert.All(Slots(rules: rules, reservations: new[] { fresh }), s => Assert.Equal(10, s.RemainingCovers));
        Assert.All(Slots(rules: rules, reservations: new[] { expired }), s => Assert.Equal(20, s.RemainingCovers));
    }

    [Fact]
    public void Availability_CancelledAndNoShowDontCount()
    {
        var slots = Slots(reservations: new[]
        {
            Res(5, ReservationStatus.Cancelled), Res(5, ReservationStatus.NoShow), Res(5, ReservationStatus.Expired),
            Res(3, ReservationStatus.Confirmed, service: "Midi"),
        });
        Assert.All(slots, s => Assert.Equal(20, s.RemainingCovers));
    }

    [Fact]
    public void Availability_PartyTooBig_NoSlot()
    {
        var reservations = new[] { Res(15, ReservationStatus.Confirmed) };
        Assert.Empty(Slots(reservations: reservations, party: 6));
        Assert.NotEmpty(Slots(reservations: reservations, party: 5));
    }
}
