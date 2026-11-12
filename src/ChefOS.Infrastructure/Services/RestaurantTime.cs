using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Le calendrier du restaurant courant, pour les pages. Le fuseau est lu une fois par portée (le restaurant d'une portée ne change pas) ;
/// l'instant, lui, est relu à chaque appel.
/// </summary>
public sealed class RestaurantTime(IDbContextFactory<ChefOsDbContext> factory, TimeProvider clock)
{
    private string? _timeZoneId;

    public async Task<RestaurantCalendar> GetAsync(CancellationToken ct = default)
    {
        if (_timeZoneId is null)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            _timeZoneId = await db.CurrentTimeZoneIdAsync(ct);
        }

        return new RestaurantCalendar(_timeZoneId, clock.GetUtcNow().UtcDateTime);
    }
}
