namespace ChefOS.Core.Vitrine;

/// <summary>Créneau réservable ; <paramref name="RemainingCovers"/> est le restant du service entier.</summary>
public record Slot(DateOnly Date, TimeOnly Time, string ServiceName, DateTime StartsAtUtc, int RemainingCovers);

public record BookedCovers(string ServiceName, DateOnly Date, int Covers);

/// <summary>Calcul pur des créneaux disponibles d'un jour.</summary>
public static class AvailabilityCalculator
{
    /// <summary>
    /// Créneaux du jour <paramref name="date"/>, triés par heure. Exclut les jours fermés, les créneaux trop proches
    /// (avant <c>utcNow + MinLeadMinutes</c>, borne incluse) ou trop lointains (date locale &gt; aujourd'hui + <c>MaxDaysAhead</c>),
    /// et ceux dont le restant du service est inférieur à <paramref name="partySize"/>.
    /// Comptent dans l'occupation : <c>Confirmed</c>, <c>Seated</c>, et <c>PendingPayment</c> créée il y a moins de <c>PendingPaymentMinutes</c>.
    /// </summary>
    public static IReadOnlyList<Slot> SlotsFor(
        DateOnly date,
        IReadOnlyList<ServiceSchedule> schedules,
        IReadOnlyList<ClosureDay> closures,
        IReadOnlyList<(DateTime StartsAtUtc, string ServiceName, int PartySize, ReservationStatus Status, DateTime CreatedAt)> reservations,
        VitrineProfile rules,
        string timeZoneId,
        DateTime utcNow,
        int partySize)
    {
        var result = new List<Slot>();

        if (closures.Any(c => c.DateLocal == date))
            return result;
        if (date > Today(utcNow, timeZoneId).AddDays(rules.MaxDaysAhead))
            return result;

        var earliestUtc = utcNow.AddMinutes(rules.MinLeadMinutes);
        var pendingLimit = TimeSpan.FromMinutes(rules.PendingPaymentMinutes);

        foreach (var schedule in schedules.Where(s => s.DayOfWeek == date.DayOfWeek && s.SlotMinutes > 0))
        {
            var booked = 0;
            foreach (var r in reservations)
            {
                if (r.ServiceName != schedule.Name)
                    continue;
                if (RestaurantClock.ToLocal(r.StartsAtUtc, timeZoneId).Date != date)
                    continue;
                var counts = r.Status is ReservationStatus.Confirmed or ReservationStatus.Seated
                    || (r.Status == ReservationStatus.PendingPayment && utcNow - r.CreatedAt < pendingLimit);
                if (counts)
                    booked += r.PartySize;
            }

            var remaining = schedule.Capacity - booked;
            if (remaining < partySize)
                continue;

            var startMin = schedule.StartLocal.Hour * 60 + schedule.StartLocal.Minute;
            var endMin = schedule.EndLocal.Hour * 60 + schedule.EndLocal.Minute;
            for (var m = startMin; m < endMin; m += schedule.SlotMinutes)
            {
                var time = new TimeOnly(m / 60, m % 60);
                var startsAtUtc = RestaurantClock.ToUtc(date, time, timeZoneId);
                if (startsAtUtc < earliestUtc)
                    continue;
                result.Add(new Slot(date, time, schedule.Name, startsAtUtc, remaining));
            }
        }

        return result.OrderBy(s => s.Time).ToList();
    }

    private static DateOnly Today(DateTime utcNow, string tz) => RestaurantClock.Today(utcNow, tz);
}
