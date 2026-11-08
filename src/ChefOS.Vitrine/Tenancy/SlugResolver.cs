using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Vitrine.Tenancy;

/// <summary>Le restaurant d'un lien public.</summary>
public sealed record RestaurantRef(int Id, string Slug, string Name, string TimeZoneId);

/// <summary>
/// Traduit le slug d'un lien en restaurant, seulement si sa vitrine est <b>publiée</b> : un slug inconnu et une vitrine non publiée sont indiscernables
/// (le public ne doit pas pouvoir deviner l'existence d'un restaurant). La lecture n'est pas filtrée par restaurant : c'est elle qui le détermine.
/// </summary>
public sealed class SlugResolver(IDbContextFactory<ChefOsDbContext> factory, SlugTenantContext tenant)
{
    private const int MaxSlugLength = 100;

    private RestaurantRef? _entered;

    /// <summary>Le restaurant courant de ce scope, une fois <see cref="EnterAsync"/> réussi.</summary>
    public RestaurantRef? Current => _entered;

    public async Task<RestaurantRef?> ResolveAsync(string? slug, CancellationToken ct = default)
    {
        var normalized = slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxSlugLength)
            return null;

        await using var db = await factory.CreateDbContextAsync(ct);

        return await (
                from r in db.Restaurants.AsNoTracking()
                join p in db.VitrineProfiles.IgnoreQueryFilters().AsNoTracking() on r.Id equals p.RestaurantId
                where r.Slug == normalized && p.IsPublished
                select new RestaurantRef(r.Id, r.Slug, r.Name, r.TimeZoneId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Résout le slug et fixe le restaurant courant du scope ; null (et aucun restaurant fixé) si le lien est inconnu ou non publié.</summary>
    public async Task<RestaurantRef?> EnterAsync(string? slug, CancellationToken ct = default)
    {
        if (_entered is { } known && string.Equals(known.Slug, slug?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            tenant.RestaurantId = known.Id;
            return known;
        }

        var restaurant = await ResolveAsync(slug, ct);
        tenant.RestaurantId = restaurant?.Id;
        _entered = restaurant;
        return restaurant;
    }
}
