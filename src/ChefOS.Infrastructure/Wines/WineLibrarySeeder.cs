using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Wines;

/// <summary>
/// Charge la bibliothèque de vins partagée (ressource intégrée <c>wine-library.json</c>) dans <c>WineReferences</c>.
/// Idempotent : chaque fiche est insérée ou mise à jour d'après sa <c>Key</c>, jamais dupliquée.
/// </summary>
public sealed class WineLibrarySeeder(IDbContextFactory<ChefOsDbContext> factory, ILogger<WineLibrarySeeder> logger)
{
    private const string ResourceName = "ChefOS.Infrastructure.Wines.wine-library.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Fiches de la ressource intégrée.</summary>
    public static IReadOnlyList<WineReference> LoadLibrary()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Ressource introuvable : {ResourceName}");
        return JsonSerializer.Deserialize<List<WineReference>>(stream, JsonOptions)
            ?? throw new InvalidOperationException("La bibliothèque de vins est vide ou illisible.");
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var library = LoadLibrary();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WineReferences.ToDictionaryAsync(w => w.Key, cancellationToken);

        var added = 0;
        var updated = 0;
        foreach (var wine in library)
        {
            if (!existing.TryGetValue(wine.Key, out var current))
            {
                wine.Id = 0;
                db.WineReferences.Add(wine);
                added++;
                continue;
            }

            var entry = db.Entry(current);
            current.Name = wine.Name;
            current.Appellation = wine.Appellation;
            current.Region = wine.Region;
            current.Country = wine.Country;
            current.Color = wine.Color;
            current.Grapes = wine.Grapes;
            current.Robe = wine.Robe;
            current.Nez = wine.Nez;
            current.Bouche = wine.Bouche;
            current.Garde = wine.Garde;
            current.ServiceTemperature = wine.ServiceTemperature;
            current.Pairings = wine.Pairings;
            if (entry.State == EntityState.Modified)
                updated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Bibliothèque de vins : {Added} fiche(s) ajoutée(s), {Updated} mise(s) à jour.", added, updated);
    }
}
