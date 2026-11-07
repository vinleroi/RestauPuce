using ChefOS.Core.Vitrine;

namespace ChefOS.Tests.Core.Vitrine;

public class RestaurantClockTests
{
    private const string Tz = "Europe/Brussels";

    [Fact]
    public void Clock_DstTransition_RoundTrips()
    {
        // 25/10/2026 : le retour d'automne a eu lieu à 03:00, 19:00 est en heure d'hiver (UTC+1).
        var oct = RestaurantClock.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(19, 0), Tz);
        Assert.Equal(new DateTime(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc), oct);
        Assert.Equal(DateTimeKind.Utc, oct.Kind);
        Assert.Equal((new DateOnly(2026, 10, 25), new TimeOnly(19, 0)), RestaurantClock.ToLocal(oct, Tz));

        // 29/03/2026 : 19:00 est déjà en heure d'été (UTC+2).
        var mar = RestaurantClock.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(19, 0), Tz);
        Assert.Equal(new DateTime(2026, 3, 29, 17, 0, 0, DateTimeKind.Utc), mar);
        Assert.Equal((new DateOnly(2026, 3, 29), new TimeOnly(19, 0)), RestaurantClock.ToLocal(mar, Tz));

        // Heure d'hiver : 19:00 le 26/10 = 18:00 UTC.
        var winter = RestaurantClock.ToUtc(new DateOnly(2026, 10, 26), new TimeOnly(19, 0), Tz);
        Assert.Equal(new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc), winter);
    }

    [Fact]
    public void Clock_NonExistentTime_ShiftsForward()
    {
        // 02:30 le 29/03/2026 n'existe pas : devient 03:30 locale (UTC+2) = 01:30 UTC.
        var utc = RestaurantClock.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), Tz);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal((new DateOnly(2026, 3, 29), new TimeOnly(3, 30)), RestaurantClock.ToLocal(utc, Tz));
    }

    [Fact]
    public void Clock_AmbiguousTime_TakesFirstOccurrence()
    {
        // 02:30 le 25/10/2026 existe deux fois ; la première est à UTC+2 = 00:30 UTC.
        var utc = RestaurantClock.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Tz);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Clock_Today_UsesLocalDate()
    {
        var utcNow = new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2027, 1, 1), RestaurantClock.Today(utcNow, Tz));
    }
}
