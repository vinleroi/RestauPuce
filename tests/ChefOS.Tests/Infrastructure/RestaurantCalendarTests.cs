using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Infrastructure;

public class RestaurantCalendarTests
{
    private const string Brussels = "Europe/Brussels";

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Today_IsTheLocalDate()
    {
        var calendar = new RestaurantCalendar(Brussels, Utc(2026, 11, 12, 23, 30));

        Assert.Equal(Utc(2026, 11, 13), calendar.Today);
        Assert.Equal(DateTimeKind.Utc, calendar.Today.Kind);
    }

    [Fact]
    public void DayOf_ConvertsTheInstantToTheLocalDay()
    {
        var calendar = new RestaurantCalendar(Brussels, Utc(2026, 11, 12));

        Assert.Equal(Utc(2026, 11, 12), calendar.DayOf(Utc(2026, 11, 12, 22, 59)));
        Assert.Equal(Utc(2026, 11, 13), calendar.DayOf(Utc(2026, 11, 12, 23, 0)));
    }

    [Fact]
    public void UtcBounds_CoverTheShortSpringDay()
    {
        var calendar = new RestaurantCalendar(Brussels, Utc(2026, 3, 29));

        Assert.Equal((Utc(2026, 3, 28, 23), Utc(2026, 3, 29, 22)), calendar.UtcBounds(Utc(2026, 3, 29)));
    }

    [Fact]
    public void UtcBounds_CoverTheLongAutumnDay()
    {
        var calendar = new RestaurantCalendar(Brussels, Utc(2026, 10, 25));

        Assert.Equal((Utc(2026, 10, 24, 22), Utc(2026, 10, 25, 23)), calendar.UtcBounds(Utc(2026, 10, 25)));
    }

    [Fact]
    public void DayOf_TheRepeatedAutumnHour_StaysOnTheSameDay()
    {
        var calendar = new RestaurantCalendar(Brussels, Utc(2026, 10, 25));

        Assert.Equal(Utc(2026, 10, 25), calendar.DayOf(Utc(2026, 10, 25, 0, 30)));
        Assert.Equal(Utc(2026, 10, 25), calendar.DayOf(Utc(2026, 10, 25, 1, 30)));
    }

    [Fact]
    public void Auckland_HasItsOwnToday()
    {
        var now = Utc(2026, 11, 12, 11, 30);

        Assert.Equal(Utc(2026, 11, 13), new RestaurantCalendar("Pacific/Auckland", now).Today);
        Assert.Equal(Utc(2026, 11, 12), new RestaurantCalendar(Brussels, now).Today);
    }

    [Fact]
    public void IsInTheFuture_AllowsLocalTomorrow()
    {
        var calendar = new RestaurantCalendar(Brussels, Utc(2026, 11, 12, 23, 30));

        Assert.False(calendar.IsInTheFuture(Utc(2026, 11, 14)));
        Assert.True(calendar.IsInTheFuture(Utc(2026, 11, 15)));
    }

    [Fact]
    public void ToLocal_ReturnsUnspecifiedLocalTime()
    {
        var local = new RestaurantCalendar(Brussels, Utc(2026, 7, 1)).ToLocal(Utc(2026, 7, 1, 10));

        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }
}
