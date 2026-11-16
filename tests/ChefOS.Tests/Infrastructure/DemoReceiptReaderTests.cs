using System.Diagnostics;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class DemoReceiptReaderTests(PostgresFixture postgres)
{
    // 23:30 UTC le 12/11/2026 : à Bruxelles, il est déjà le 13 (le serveur peut être en UTC).
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 12, 23, 30, 0, TimeSpan.Zero));

    private static Task<ReceiptExtraction> ReadAsync(CancellationToken ct = default) =>
        new DemoReceiptReader(Clock).ReadAsync([1, 2, 3], "image/jpeg", ct);

    [Fact]
    public async Task DemoReader_ReturnsLines()
    {
        var clock = Stopwatch.StartNew();
        var extraction = await ReadAsync();
        clock.Stop();

        Assert.Equal("Metro Cash & Carry", extraction.SupplierName);
        Assert.Equal(new DateOnly(2026, 11, 13), extraction.Date);
        Assert.False(extraction.AmountsIncludeVat);

        Assert.True(extraction.Lines.Count >= 6);
        Assert.All(extraction.Lines, line =>
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Label));
            Assert.True(line.Quantity > 0m, line.Label);
            Assert.NotNull(line.Unit);
            Assert.True(line.LineTotal > 0m, line.Label);
            Assert.InRange(line.VatRate ?? 0m, 0m, 1m);
        });

        Assert.Contains(extraction.Lines, l => l.Label == "BEURRE DOUX 82% 1KG");
        Assert.Contains(extraction.Lines, l => l.Label == "OIGNONS JAUNES FILET 5KG");
        Assert.Contains(extraction.Lines, l => l.Label == "BIERE BRUNE 33CL X24");
        Assert.Contains(extraction.Lines, l => l.Label == "SACS POUBELLE 100L");

        // L'en-tête est cohérent avec les lignes : le total est la somme des lignes (hors TVA).
        Assert.Equal(extraction.Lines.Sum(l => l.LineTotal), extraction.Total);

        // Une lecture simulée prend un peu de temps, comme une vraie.
        Assert.True(clock.ElapsedMilliseconds >= 700, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task DemoReader_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(cts.Token));
    }

    /// <summary>
    /// Les libellés du ticket de démonstration doivent rester en phase avec le seed du Petit Bistro : un renommage d'ingrédient ou une
    /// révision de prix ferait perdre la démonstration (lignes sans correspondance, plus de hausse visible).
    /// </summary>
    [Fact]
    public async Task DemoReader_AgainstSeededBistro_MatchesIngredientsAndShowsOnePriceIncrease()
    {
        var options = await postgres.CreateDatabaseAsync();
        await using var host = DemoSeederTests.BuildHost(options);
        await DemoSeederTests.SeedAsync(host);
        var restaurantId = await DemoSeederTests.GetBistroIdAsync(options);

        List<Ingredient> ingredients;
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(restaurantId)))
            ingredients = await db.Ingredients.AsNoTracking().ToListAsync();

        var extraction = await ReadAsync();

        var matched = new List<(string Label, decimal Change)>();
        var unmatched = new List<string>();
        foreach (var line in extraction.Lines)
        {
            var suggestion = IngredientMatcher.Suggest(line.Label, null, [], ingredients);
            if (suggestion is null)
            {
                unmatched.Add(line.Label);
                continue;
            }

            var ingredient = ingredients.Single(i => i.Id == suggestion.IngredientId);
            var price = ReceiptLinePricing.Compute(line.Quantity, line.Unit, line.LineTotal, ingredient);
            Assert.Equal(ReceiptLineIssue.None, price.Issue);

            matched.Add((line.Label, price.PricePerBaseUnit!.Value / ingredient.CurrentPricePerBaseUnit!.Value - 1m));
        }

        Assert.Equal(["SACS POUBELLE 100L"], unmatched);
        Assert.True(matched.Count >= 5, $"{matched.Count} lignes rapprochées");

        // Une seule hausse d'environ 10 %, les autres lignes restent proches du prix du seed.
        var increases = matched.Where(m => m.Change is >= 0.08m and <= 0.12m).ToList();
        Assert.Single(increases);
        Assert.All(matched.Except(increases), m => Assert.InRange(m.Change, -0.06m, 0.06m));
    }
}
