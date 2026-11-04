using ChefOS.Back.Components.Pages.Stats;

namespace ChefOS.Tests.Back;

public class StatsPeriodTests
{
    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static void AssertPeriod(DateTime from, DateTime to, StatsPeriod period)
    {
        Assert.Equal(from, period.From);
        Assert.Equal(to, period.To);
        Assert.Equal(DateTimeKind.Utc, period.From.Kind);
        Assert.Equal(DateTimeKind.Utc, period.To.Kind);
    }

    // 2026-11-02 est un lundi : 11-04 = mercredi, 11-08 = dimanche.

    [Theory]
    [InlineData(2026, 11, 4, 2026, 11, 2, 2026, 11, 4)]     // mercredi : lundi → aujourd'hui
    [InlineData(2026, 11, 2, 2026, 11, 2, 2026, 11, 2)]     // lundi : un seul jour
    [InlineData(2026, 11, 8, 2026, 11, 2, 2026, 11, 8)]     // dimanche : la semaine entière
    [InlineData(2026, 11, 1, 2026, 10, 26, 2026, 11, 1)]    // dimanche : la semaine commence le lundi d'avant, à cheval sur deux mois
    public void ThisWeek_RunsFromMondayToToday(int y, int m, int d, int fy, int fm, int fd, int ty, int tm, int td)
    {
        var period = StatsPeriod.Of(StatsPreset.ThisWeek, Day(y, m, d));

        AssertPeriod(Day(fy, fm, fd), Day(ty, tm, td), period);
    }

    [Theory]
    [InlineData(2026, 11, 4, 2026, 10, 26, 2026, 11, 1)]    // mercredi
    [InlineData(2026, 11, 2, 2026, 10, 26, 2026, 11, 1)]    // lundi : la semaine qui vient de finir
    [InlineData(2026, 11, 8, 2026, 10, 26, 2026, 11, 1)]    // dimanche
    [InlineData(2027, 1, 6, 2026, 12, 28, 2027, 1, 3)]      // à cheval sur deux années
    public void LastWeek_IsTheFullMondayToSundayBeforeThisWeek(int y, int m, int d, int fy, int fm, int fd, int ty, int tm, int td)
    {
        var period = StatsPeriod.Of(StatsPreset.LastWeek, Day(y, m, d));

        AssertPeriod(Day(fy, fm, fd), Day(ty, tm, td), period);
    }

    [Theory]
    [InlineData(2026, 11, 4, 2026, 11, 1, 2026, 11, 4)]
    [InlineData(2026, 11, 1, 2026, 11, 1, 2026, 11, 1)]     // le 1er : un seul jour
    [InlineData(2026, 2, 28, 2026, 2, 1, 2026, 2, 28)]
    public void ThisMonth_RunsFromTheFirstToToday(int y, int m, int d, int fy, int fm, int fd, int ty, int tm, int td)
    {
        var period = StatsPeriod.Of(StatsPreset.ThisMonth, Day(y, m, d));

        AssertPeriod(Day(fy, fm, fd), Day(ty, tm, td), period);
    }

    [Theory]
    [InlineData(2026, 11, 4, 2026, 10, 1, 2026, 10, 31)]
    [InlineData(2026, 11, 1, 2026, 10, 1, 2026, 10, 31)]
    [InlineData(2027, 1, 15, 2026, 12, 1, 2026, 12, 31)]    // janvier : décembre de l'année d'avant
    [InlineData(2028, 3, 10, 2028, 2, 1, 2028, 2, 29)]      // février d'une année bissextile
    [InlineData(2026, 3, 31, 2026, 2, 1, 2026, 2, 28)]
    [InlineData(2026, 5, 31, 2026, 4, 1, 2026, 4, 30)]      // mois précédent plus court que le courant
    public void LastMonth_IsTheWholePreviousMonth(int y, int m, int d, int fy, int fm, int fd, int ty, int tm, int td)
    {
        var period = StatsPeriod.Of(StatsPreset.LastMonth, Day(y, m, d));

        AssertPeriod(Day(fy, fm, fd), Day(ty, tm, td), period);
    }

    [Fact]
    public void Last4Weeks_IsTheTwentyEightDaysEndingToday()
    {
        var period = StatsPeriod.Of(StatsPreset.Last4Weeks, Day(2026, 11, 4));

        AssertPeriod(Day(2026, 10, 8), Day(2026, 11, 4), period);
        Assert.Equal(28, period.Days);
    }

    [Fact]
    public void Last4Weeks_CrossesYearEnd()
    {
        var period = StatsPeriod.Of(StatsPreset.Last4Weeks, Day(2027, 1, 10));

        AssertPeriod(Day(2026, 12, 14), Day(2027, 1, 10), period);
    }

    [Fact]
    public void Days_CountsBothBounds()
    {
        Assert.Equal(1, new StatsPeriod(Day(2026, 11, 1), Day(2026, 11, 1)).Days);
        Assert.Equal(7, StatsPeriod.Of(StatsPreset.LastWeek, Day(2026, 11, 4)).Days);
        Assert.Equal(31, StatsPeriod.Of(StatsPreset.LastMonth, Day(2026, 11, 4)).Days);
    }

    [Fact]
    public void Of_IgnoresTheTimeOfDay()
    {
        var evening = new DateTime(2026, 11, 4, 23, 59, 59, DateTimeKind.Utc);

        AssertPeriod(Day(2026, 10, 8), Day(2026, 11, 4), StatsPeriod.Of(StatsPreset.Last4Weeks, evening));
    }

    [Fact]
    public void Of_ReadsALocalInstantAsItsUtcDay()
    {
        // 23 h 30 UTC le 4 novembre : selon le fuseau de la machine, la date locale est déjà le 5 ; le jour UTC reste le 4.
        var local = new DateTime(2026, 11, 4, 23, 30, 0, DateTimeKind.Utc).ToLocalTime();

        AssertPeriod(Day(2026, 11, 2), Day(2026, 11, 4), StatsPeriod.Of(StatsPreset.ThisWeek, local));
    }

    [Fact]
    public void Of_ReadsAnUnspecifiedDateAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Unspecified);

        AssertPeriod(Day(2026, 11, 2), Day(2026, 11, 4), StatsPeriod.Of(StatsPreset.ThisWeek, unspecified));
    }

    [Fact]
    public void Of_Custom_HasNoRangeOfItsOwn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatsPeriod.Of(StatsPreset.Custom, Day(2026, 11, 4)));
    }

    [Fact]
    public void Of_UnknownPreset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatsPeriod.Of((StatsPreset)99, Day(2026, 11, 4)));
    }

    [Fact]
    public void Presets_ListsEveryShortcutInDisplayOrderWithCustomLast()
    {
        Assert.Equal(
            [StatsPreset.ThisWeek, StatsPreset.LastWeek, StatsPreset.ThisMonth, StatsPreset.LastMonth, StatsPreset.Last4Weeks, StatsPreset.Custom],
            StatsPeriod.Presets);
    }

    [Theory]
    [InlineData(StatsPreset.ThisWeek, "Cette semaine")]
    [InlineData(StatsPreset.LastWeek, "Semaine dernière")]
    [InlineData(StatsPreset.ThisMonth, "Ce mois")]
    [InlineData(StatsPreset.LastMonth, "Mois dernier")]
    [InlineData(StatsPreset.Last4Weeks, "4 dernières semaines")]
    [InlineData(StatsPreset.Custom, "Personnalisée")]
    public void Label_IsInFrench(StatsPreset preset, string expected)
    {
        Assert.Equal(expected, StatsPeriod.Label(preset));
    }

    [Fact]
    public void Label_UnknownPreset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StatsPeriod.Label((StatsPreset)99));
    }

    [Fact]
    public void Text_WritesBothBoundsAsDayMonthYear()
    {
        Assert.Equal("du 08/10/2026 au 04/11/2026", StatsPeriod.Of(StatsPreset.Last4Weeks, Day(2026, 11, 4)).Text);
        Assert.Equal("du 02/11/2026 au 02/11/2026", StatsPeriod.Of(StatsPreset.ThisWeek, Day(2026, 11, 2)).Text);
    }
}
