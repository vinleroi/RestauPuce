using ChefOS.Back.Components.Pages.Stats;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class StatsViewTests
{
    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static VarianceRow Row(int id, string name, decimal? value) =>
        new(id, name, BaseUnit.Gram, 0m, 0m, 0m, 0m, 0m, 0m, value);

    // ── Écart d'inventaire : les premières lignes ────────────────────────────

    [Fact]
    public void TopVariance_KeepsTheFifteenLargestValuesFirst()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Row(i, $"Ingrédient {i:00}", (i * 7 % 20) - 5)).ToList();

        var top = StatsView.TopVariance(rows);

        Assert.Equal(15, top.Count);
        Assert.Equal(top.OrderByDescending(r => r.VarianceValue).Select(r => r.IngredientId), top.Select(r => r.IngredientId));
        // Ce qui est écarté est plus petit que ce qui est gardé.
        var dropped = rows.Except(top).ToList();
        Assert.All(dropped, r => Assert.True(r.VarianceValue <= top[^1].VarianceValue));
    }

    [Fact]
    public void TopVariance_PutsUnknownValuesLast_AndBreaksTiesByName()
    {
        var rows = new[]
        {
            Row(1, "Sel", null), Row(2, "Poivre", 4m), Row(3, "Ail", 4m), Row(4, "Sucre", -2m), Row(5, "Beurre", 10m)
        };

        var top = StatsView.TopVariance(rows);

        Assert.Equal(["Beurre", "Ail", "Poivre", "Sucre", "Sel"], top.Select(r => r.Name));
    }

    [Fact]
    public void TopVariance_FewerRowsThanTheLimit_ReturnsThemAll()
    {
        Assert.Equal(2, StatsView.TopVariance([Row(1, "A", 1m), Row(2, "B", 2m)]).Count);
        Assert.Empty(StatsView.TopVariance([]));
    }

    [Fact]
    public void TopVariance_HonoursACustomLimit()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Row(i, $"I{i}", i)).ToList();

        Assert.Equal([10m, 9m, 8m], StatsView.TopVariance(rows, 3).Select(r => r.VarianceValue!.Value));
    }

    // ── Écart d'inventaire : un total partiel n'est pas présenté comme exact ──

    private static VarianceReport Report(params VarianceRow[] rows) =>
        new(Day(2026, 10, 7), Day(2026, 11, 4), rows, rows.Sum(r => r.VarianceValue ?? 0m), [], []);

    [Fact]
    public void VarianceIncomplete_AllRowsPriced_IsFalse()
    {
        Assert.False(StatsView.VarianceIncomplete(Report(Row(1, "Beurre", 12m), Row(2, "Sel", -3m), Row(3, "Eau", 0m))));
    }

    [Fact]
    public void VarianceIncomplete_AnyRowWithoutAPrice_IsTrue()
    {
        // La valeur d'une ligne sans prix compte pour 0 dans le total et le coût réel : ces deux chiffres sont alors partiels.
        Assert.True(StatsView.VarianceIncomplete(Report(Row(1, "Beurre", 12m), Row(2, "Safran", null))));
        Assert.True(StatsView.VarianceIncomplete(Report(Row(1, "Safran", null))));
    }

    [Fact]
    public void VarianceIncomplete_NoRows_IsFalse()
    {
        Assert.False(StatsView.VarianceIncomplete(Report()));
    }

    [Fact]
    public void VarianceIncomplete_LooksAtAllRows_NotOnlyTheFirstFifteenShown()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Row(i, $"I{i:00}", 100 - i)).Append(Row(21, "Sans prix", null)).ToArray();

        Assert.DoesNotContain(StatsView.TopVariance(rows), r => r.VarianceValue is null);      // la ligne sans prix n'est pas affichée…
        Assert.True(StatsView.VarianceIncomplete(Report(rows)));                                 // …mais le total en tient compte
    }

    // ── Graphique du chiffre d'affaires ──────────────────────────────────────

    [Fact]
    public void RevenueBuckets_ShortPeriod_OneBucketPerDay()
    {
        var days = Enumerable.Range(0, 28).Select(i => new DayRevenue(Day(2026, 10, 8).AddDays(i), 100m + i, i % 3)).ToList();

        var buckets = StatsView.RevenueBuckets(days);

        Assert.Equal(28, buckets.Count);
        Assert.False(StatsView.IsWeekly(days));
        Assert.Equal("08/10", buckets[0].Label);
        Assert.Equal("04/11", buckets[^1].Label);
        Assert.Equal(100m, buckets[0].FromEntries);
        Assert.Equal(1m, buckets[1].FromOrders);
    }

    [Fact]
    public void RevenueBuckets_ExactlyTheDailyLimit_StaysDaily()
    {
        var days = Enumerable.Range(0, StatsView.MaxDailyBars).Select(i => new DayRevenue(Day(2026, 1, 1).AddDays(i), 1m, 0m)).ToList();

        Assert.Equal(StatsView.MaxDailyBars, StatsView.RevenueBuckets(days).Count);
        Assert.False(StatsView.IsWeekly(days));
    }

    [Fact]
    public void RevenueBuckets_LongPeriod_GroupsByWeekStartingMonday()
    {
        // 2026-01-01 est un jeudi : la première semaine est partielle (jeudi → dimanche), les suivantes complètes.
        var days = Enumerable.Range(0, 100).Select(i => new DayRevenue(Day(2026, 1, 1).AddDays(i), 10m, 2m)).ToList();

        var buckets = StatsView.RevenueBuckets(days);

        Assert.True(StatsView.IsWeekly(days));
        Assert.Equal(15, buckets.Count);                        // 4 jours + 13 semaines complètes (91 jours) + 5 jours
        Assert.Equal("01/01", buckets[0].Label);
        Assert.Equal(40m, buckets[0].FromEntries);              // 4 jours
        Assert.Equal(8m, buckets[0].FromOrders);
        Assert.Equal("05/01", buckets[1].Label);                // lundi suivant
        Assert.Equal(70m, buckets[1].FromEntries);
        Assert.Equal(14m, buckets[1].FromOrders);
        Assert.Equal(Day(2026, 1, 1), buckets[0].Start);
        Assert.Equal(Day(2026, 1, 5), buckets[1].Start);
    }

    [Fact]
    public void RevenueBuckets_NeverLosesRevenue()
    {
        var days = Enumerable.Range(0, 200).Select(i => new DayRevenue(Day(2026, 3, 3).AddDays(i), 12.34m + i, 3.21m)).ToList();

        var buckets = StatsView.RevenueBuckets(days);

        Assert.Equal(days.Sum(d => d.FromEntries), buckets.Sum(b => b.FromEntries));
        Assert.Equal(days.Sum(d => d.FromOrders), buckets.Sum(b => b.FromOrders));
    }

    [Fact]
    public void RevenueBuckets_NoDays_NoBuckets()
    {
        Assert.Empty(StatsView.RevenueBuckets([]));
    }

    [Fact]
    public void HasRevenue_IsTrueOnlyWhenSomeDayHasSales()
    {
        var empty = new[] { new DayRevenue(Day(2026, 11, 1), 0m, 0m), new DayRevenue(Day(2026, 11, 2), 0m, 0m) };
        var some = new[] { new DayRevenue(Day(2026, 11, 1), 0m, 0m), new DayRevenue(Day(2026, 11, 2), 0m, 5m) };

        Assert.False(StatsView.HasRevenue(empty));
        Assert.True(StatsView.HasRevenue(some));
        Assert.False(StatsView.HasRevenue([]));
    }
}
