using System.Globalization;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Stats;

/// <summary>Une barre du graphique du chiffre d'affaires : un jour, ou une semaine quand la période est longue. <see cref="Start"/> est son premier jour.</summary>
public sealed record RevenueBucket(string Label, DateTime Start, decimal FromEntries, decimal FromOrders);

/// <summary>Ce que la page des statistiques tire des chiffres du service avant de les afficher (sans dépendance à l'interface, donc testé à part).</summary>
public static class StatsView
{
    /// <summary>Nombre d'écarts d'inventaire affichés.</summary>
    public const int VarianceRowLimit = 15;

    /// <summary>Au-delà de ce nombre de jours, le graphique regroupe le chiffre d'affaires par semaine (366 barres seraient illisibles).</summary>
    public const int MaxDailyBars = 62;

    /// <summary>
    /// Les écarts les plus importants, par valeur décroissante (les pertes inexpliquées d'abord, les valeurs inconnues en dernier), à valeur égale par nom.
    /// </summary>
    public static IReadOnlyList<VarianceRow> TopVariance(IReadOnlyList<VarianceRow> rows, int limit = VarianceRowLimit) =>
        rows
            .OrderBy(r => r.VarianceValue is null)
            .ThenByDescending(r => r.VarianceValue)
            .ThenBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(r => r.IngredientId)
            .Take(limit)
            .ToList();

    /// <summary>Vrai quand au moins un jour a du chiffre d'affaires (sinon le graphique serait vide).</summary>
    public static bool HasRevenue(IReadOnlyList<DayRevenue> days) => days.Any(d => d.FromEntries != 0m || d.FromOrders != 0m);

    /// <summary>Vrai quand le graphique regroupe les jours par semaine (période de plus de <see cref="MaxDailyBars"/> jours).</summary>
    public static bool IsWeekly(IReadOnlyList<DayRevenue> days) => days.Count > MaxDailyBars;

    /// <summary>
    /// Les barres du graphique : un jour par barre, ou, au-delà de <see cref="MaxDailyBars"/> jours, une semaine (du lundi au dimanche ; la première peut commencer
    /// en cours de semaine, comme la période). Le chiffre d'affaires est conservé à l'identique, seul le regroupement change.
    /// </summary>
    public static IReadOnlyList<RevenueBucket> RevenueBuckets(IReadOnlyList<DayRevenue> days)
    {
        if (!IsWeekly(days))
            return days.Select(d => new RevenueBucket(Label(d.Day), d.Day, d.FromEntries, d.FromOrders)).ToList();

        return days
            .GroupBy(d => d.Day.AddDays(-(((int)d.Day.DayOfWeek + 6) % 7)))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var start = g.Min(d => d.Day);
                return new RevenueBucket(Label(start), start, g.Sum(d => d.FromEntries), g.Sum(d => d.FromOrders));
            })
            .ToList();
    }

    private static string Label(DateTime day) => day.ToString("dd/MM", CultureInfo.InvariantCulture);
}
