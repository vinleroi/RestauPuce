using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Vitrine.Tenancy;

/// <summary>Le restaurant d'un lien public. <paramref name="IsPublished"/> : faux seulement pour le lien d'une réservation d'une vitrine non publiée.</summary>
public sealed record RestaurantRef(int Id, string Slug, string Name, string TimeZoneId, bool IsPublished = true);

/// <summary>
/// Traduit le slug d'un lien en restaurant, seulement si sa vitrine est <b>publiée</b> : un slug inconnu et une vitrine non publiée sont indiscernables
/// (le public ne doit pas pouvoir deviner l'existence d'un restaurant). La lecture n'est pas filtrée par restaurant : c'est elle qui le détermine.
/// Seule exception : le lien personnel d'une réservation (<see cref="EnterReservationLinkAsync"/>), où c'est le jeton qui désigne le restaurant.
/// </summary>
public sealed class SlugResolver(IDbContextFactory<ChefOsDbContext> factory, SlugTenantContext tenant)
{
    private const int MaxSlugLength = 100;
    private const int MaxTokenLength = 64;

    private RestaurantRef? _entered;

    /// <summary>Le restaurant courant de ce scope, une fois <see cref="EnterAsync"/> ou <see cref="EnterReservationLinkAsync"/> réussi.</summary>
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
                select new RestaurantRef(r.Id, r.Slug, r.Name, r.TimeZoneId, true))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Résout le slug et fixe le restaurant courant du scope ; null (et aucun restaurant fixé) si le lien est inconnu ou non publié.</summary>
    public async Task<RestaurantRef?> EnterAsync(string? slug, CancellationToken ct = default)
    {
        // Un restaurant entré par le jeton d'une réservation n'ouvre pas les autres pages : seule une vitrine publiée se réutilise ici.
        if (_entered is { IsPublished: true } known && IsSameSlug(slug, known.Slug))
        {
            tenant.RestaurantId = known.Id;
            return known;
        }

        var restaurant = await ResolveAsync(slug, ct);
        tenant.RestaurantId = restaurant?.Id;
        _entered = restaurant;
        return restaurant;
    }

    /// <summary>
    /// Lien personnel <c>/{slug}/reservation/{jeton}</c> (page et annulation qu'elle permet) : le restaurant est celui de la réservation du jeton, que sa vitrine
    /// soit publiée ou non (réservation manuelle d'un restaurant sans vitrine en ligne). Le restaurant courant n'est fixé que si <paramref name="slug"/> est son slug
    /// actuel : sinon (slug changé depuis, ou jeton d'un autre restaurant) l'appelant redirige vers le bon slug, et rien n'est montré sous celui du lien.
    /// Renvoie null pour un jeton inconnu ; aucun restaurant n'est alors fixé.
    /// </summary>
    public async Task<RestaurantRef?> EnterReservationLinkAsync(string? slug, string? token, CancellationToken ct = default)
    {
        var restaurant = await ResolveReservationTokenAsync(token, ct);
        var entered = restaurant is not null && IsSameSlug(slug, restaurant.Slug) ? restaurant : null;
        tenant.RestaurantId = entered?.Id;
        _entered = entered;
        return restaurant;
    }

    /// <summary>Le slug d'un lien désigne-t-il ce restaurant (casse et espaces ignorées, comme pour la résolution) ?</summary>
    public static bool IsSameSlug(string? requested, string actual) =>
        string.Equals(requested?.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lecture sans filtre de restaurant (c'est elle qui le détermine) : la réservation du jeton, son restaurant, et si sa vitrine est publiée.</summary>
    private async Task<RestaurantRef?> ResolveReservationTokenAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return null;

        await using var db = await factory.CreateDbContextAsync(ct);

        return await (
                from reservation in db.Reservations.IgnoreQueryFilters().AsNoTracking()
                join r in db.Restaurants.AsNoTracking() on reservation.RestaurantId equals r.Id
                where reservation.PublicToken == token
                select new RestaurantRef(r.Id, r.Slug, r.Name, r.TimeZoneId,
                    db.VitrineProfiles.IgnoreQueryFilters().Any(p => p.RestaurantId == r.Id && p.IsPublished)))
            .FirstOrDefaultAsync(ct);
    }
}
