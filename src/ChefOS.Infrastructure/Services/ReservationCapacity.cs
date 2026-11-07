using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Occupation d'un service et verrou qui empêche la surréservation. Le verrou est un verrou consultatif PostgreSQL de transaction
/// (<c>pg_advisory_xact_lock(restaurant, clé)</c>) : toute écriture qui occupe des places d'un même service et d'un même jour le prend, relit le restant
/// une fois obtenu (la lecture voit donc ce que les transactions précédentes ont validé), puis insère ; il est relâché au commit ou au rollback.
/// La clé est dérivée de façon stable (SHA-256) et non de <c>string.GetHashCode()</c>, qui change d'un processus à l'autre.
/// </summary>
internal static class ReservationCapacity
{
    /// <summary>Les 4 premiers octets (big-endian) du SHA-256 de « service|aaaa-mm-jj ».</summary>
    internal static int LockKey(string serviceName, DateOnly localDate)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{serviceName}|{localDate:yyyy-MM-dd}"));
        return BinaryPrimitives.ReadInt32BigEndian(hash);
    }

    /// <summary>À appeler dans une transaction déjà ouverte.</summary>
    internal static Task LockAsync(ChefOsDbContext db, int restaurantId, string serviceName, DateOnly localDate, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({restaurantId}, {LockKey(serviceName, localDate)})", ct);

    /// <summary>
    /// Couverts occupés par le service ce jour-là : <c>Confirmed</c>, <c>Seated</c> et <c>PendingPayment</c> créée il y a moins de <paramref name="pendingMinutes"/>
    /// (mêmes règles que <see cref="AvailabilityCalculator"/>), sans la réservation <paramref name="excludeReservationId"/>.
    /// </summary>
    internal static async Task<int> BookedAsync(ChefOsDbContext db, string serviceName, DateOnly localDate, string timeZoneId,
        DateTime utcNow, int pendingMinutes, int excludeReservationId, CancellationToken ct)
    {
        var from = RestaurantClock.ToUtc(localDate, TimeOnly.MinValue, timeZoneId);
        var to = RestaurantClock.ToUtc(localDate.AddDays(1), TimeOnly.MinValue, timeZoneId);
        var cutoff = utcNow.AddMinutes(-pendingMinutes);

        return await db.Reservations
            .Where(r => r.ServiceName == serviceName && r.StartsAtUtc >= from && r.StartsAtUtc < to && r.Id != excludeReservationId
                && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Seated
                    || (r.Status == ReservationStatus.PendingPayment && r.CreatedAt > cutoff)))
            .SumAsync(r => (int?)r.PartySize, ct) ?? 0;
    }

    /// <summary>Capacité du service ce jour de la semaine ; null s'il n'existe pas.</summary>
    internal static Task<int?> CapacityAsync(ChefOsDbContext db, string serviceName, DayOfWeek day, CancellationToken ct) =>
        db.ServiceSchedules
            .Where(s => s.Name == serviceName && s.DayOfWeek == day)
            .Select(s => (int?)s.Capacity)
            .FirstOrDefaultAsync(ct);

    /// <summary>Restant du service (capacité − occupation) ; 0 si le service n'existe pas.</summary>
    internal static async Task<int> RemainingAsync(ChefOsDbContext db, string serviceName, DateOnly localDate, string timeZoneId,
        DateTime utcNow, int pendingMinutes, int excludeReservationId, CancellationToken ct)
    {
        var capacity = await CapacityAsync(db, serviceName, localDate.DayOfWeek, ct);
        if (capacity is null)
            return 0;
        return capacity.Value - await BookedAsync(db, serviceName, localDate, timeZoneId, utcNow, pendingMinutes, excludeReservationId, ct);
    }
}
