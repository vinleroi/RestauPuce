using System.Globalization;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Stats;

/// <summary>
/// Les raccourcis de période de la page des statistiques ; <see cref="BetweenCounts"/> prend les dates des deux derniers inventaires clôturés,
/// <see cref="Custom"/> laisse le chef choisir ses deux dates.
/// </summary>
public enum StatsPreset
{
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    Last4Weeks,
    BetweenCounts,
    Custom
}

/// <summary>
/// Une période de statistiques, du jour <see cref="From"/> au jour <see cref="To"/> inclus. Les jours sont des jours du calendrier UTC (minuit, <see cref="DateTimeKind.Utc"/>),
/// comme dans <c>StatsService</c>. Les raccourcis se calculent depuis « aujourd'hui » passé en paramètre, ce qui les rend testables.
/// </summary>
public readonly record struct StatsPeriod(DateTime From, DateTime To)
{
    /// <summary>Les raccourcis dans l'ordre d'affichage, la période personnalisée en dernier.</summary>
    public static IReadOnlyList<StatsPreset> Presets { get; } =
        [StatsPreset.ThisWeek, StatsPreset.LastWeek, StatsPreset.ThisMonth, StatsPreset.LastMonth, StatsPreset.Last4Weeks, StatsPreset.BetweenCounts, StatsPreset.Custom];

    /// <summary>Le nombre de jours de la période, bornes comprises.</summary>
    public int Days => (int)(To - From).TotalDays + 1;

    /// <summary>« du 08/10/2026 au 04/11/2026 ».</summary>
    public string Text => $"du {Format(From)} au {Format(To)}";

    /// <summary>
    /// La période d'un raccourci, calculée depuis <paramref name="today"/> (seul son jour UTC compte) : cette semaine (du lundi à aujourd'hui), la semaine dernière
    /// (lundi à dimanche), ce mois (du 1er à aujourd'hui), le mois dernier (entier), les 4 dernières semaines (28 jours se terminant aujourd'hui).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="StatsPreset.Custom"/> n'a pas de bornes à lui : elles viennent du chef. <see cref="StatsPreset.BetweenCounts"/> non plus : elles viennent des inventaires
    /// (voir <see cref="BetweenLastCounts"/>).
    /// </exception>
    public static StatsPeriod Of(StatsPreset preset, DateTime today)
    {
        var day = UtcDay(today);

        switch (preset)
        {
            case StatsPreset.ThisWeek:
                return new StatsPeriod(MondayOf(day), day);

            case StatsPreset.LastWeek:
                var lastMonday = MondayOf(day).AddDays(-7);
                return new StatsPeriod(lastMonday, lastMonday.AddDays(6));

            case StatsPreset.ThisMonth:
                return new StatsPeriod(FirstOfMonth(day), day);

            case StatsPreset.LastMonth:
                var firstOfThisMonth = FirstOfMonth(day);
                return new StatsPeriod(firstOfThisMonth.AddMonths(-1), firstOfThisMonth.AddDays(-1));

            case StatsPreset.Last4Weeks:
                return new StatsPeriod(day.AddDays(-27), day);

            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Ce raccourci n'a pas de période propre.");
        }
    }

    /// <summary>
    /// La période entre les deux derniers inventaires clôturés, bornes comprises : du jour de l'avant-dernier au jour du dernier, c'est-à-dire l'intervalle sur lequel
    /// l'écart d'inventaire et le food cost réel se calculent. Le dernier est le plus récent par date (à date égale, le dernier créé) ; l'avant-dernier est le plus
    /// récent des clôturés d'un jour strictement plus ancien (deux inventaires du même jour ne délimitent aucun intervalle). Null quand il n'y a pas deux jours
    /// d'inventaires clôturés : les brouillons ne comptent pas.
    /// </summary>
    public static StatsPeriod? BetweenLastCounts(IEnumerable<StockCountSummary> counts)
    {
        var closed = counts
            .Where(c => c.Status == StockCountStatus.Cloture)
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.Id)
            .ToList();
        if (closed.Count == 0)
            return null;

        var closingDay = UtcDay(closed[0].Date);
        var opening = closed.FirstOrDefault(c => UtcDay(c.Date) < closingDay);

        return opening is null ? null : new StatsPeriod(UtcDay(opening.Date), closingDay);
    }

    public static string Label(StatsPreset preset) => preset switch
    {
        StatsPreset.ThisWeek => "Cette semaine",
        StatsPreset.LastWeek => "Semaine dernière",
        StatsPreset.ThisMonth => "Ce mois",
        StatsPreset.LastMonth => "Mois dernier",
        StatsPreset.Last4Weeks => "4 dernières semaines",
        StatsPreset.BetweenCounts => "Entre les deux derniers inventaires",
        StatsPreset.Custom => "Personnalisée",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    /// <summary>Minuit UTC du jour de <paramref name="value"/> ; un instant local est ramené en UTC, une date sans fuseau est lue comme étant en UTC.</summary>
    public static DateTime UtcDay(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
    }

    private static DateTime MondayOf(DateTime day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateTime FirstOfMonth(DateTime day) => new(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Format(DateTime day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
