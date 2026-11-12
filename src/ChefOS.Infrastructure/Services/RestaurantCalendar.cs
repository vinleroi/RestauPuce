using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Les jours d'un restaurant, dans son fuseau (<c>Restaurant.TimeZoneId</c>), à un instant donné. Un « jour » est une étiquette de calendrier :
/// minuit UTC du jour local (<see cref="DateTimeKind.Utc"/>), comme les dates stockées en base (<c>SalesEntry.Date</c>, etc.), qui ne sont jamais converties.
/// Un instant (<c>Order.CreatedAt</c>, etc.) est un vrai moment UTC : on le convertit dans le fuseau du restaurant pour le grouper par jour ou l'afficher.
/// Les conversions (heure inexistante, heure ambiguë) sont celles de <see cref="RestaurantClock"/>.
/// </summary>
public sealed class RestaurantCalendar
{
    public RestaurantCalendar(string timeZoneId, DateTime utcNow)
    {
        TimeZoneId = timeZoneId;
        UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }

    public string TimeZoneId { get; }

    /// <summary>L'instant de référence, en UTC.</summary>
    public DateTime UtcNow { get; }

    /// <summary>Aujourd'hui : la date locale du restaurant, à 00:00, étiquette de jour (<see cref="DateTimeKind.Utc"/>).</summary>
    public DateTime Today => DayOf(UtcNow);

    /// <summary>Le jour local d'un instant UTC, comme étiquette de jour.</summary>
    public DateTime DayOf(DateTime instantUtc) =>
        DateTime.SpecifyKind(RestaurantClock.ToLocal(instantUtc, TimeZoneId).Date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>
    /// Les instants UTC qui bornent le jour local <paramref name="day"/> : de 00:00 locale incluse à 00:00 locale du lendemain exclue.
    /// Un jour de changement d'heure dure 23 ou 25 heures.
    /// </summary>
    public (DateTime FromUtc, DateTime ToUtc) UtcBounds(DateTime day)
    {
        var date = DateOnly.FromDateTime(day);
        return (RestaurantClock.ToUtc(date, TimeOnly.MinValue, TimeZoneId), RestaurantClock.ToUtc(date.AddDays(1), TimeOnly.MinValue, TimeZoneId));
    }

    /// <summary>Un instant UTC en heure locale du restaurant, pour l'affichage (<see cref="DateTimeKind.Unspecified"/>).</summary>
    public DateTime ToLocal(DateTime instantUtc)
    {
        var (date, time) = RestaurantClock.ToLocal(instantUtc, TimeZoneId);
        return DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Vrai pour un jour postérieur à demain (dans le fuseau du restaurant). Demain reste permis : un bon de livraison peut être daté du lendemain. Une date plus lointaine est une
    /// erreur de saisie ou de lecture (année mal lue) : prise comme référence, elle rendrait « en retard » tous les tickets réels suivants,
    /// dont les prix n'iraient plus qu'à l'historique.
    /// </summary>
    public bool IsInTheFuture(DateTime day) => day.Date > Today.AddDays(1);

    /// <summary>Le calendrier du restaurant courant du contexte, à l'instant donné par <paramref name="clock"/>.</summary>
    public static async Task<RestaurantCalendar> ForCurrentRestaurantAsync(ChefOsDbContext db, TimeProvider clock, CancellationToken ct = default) =>
        new(await db.CurrentTimeZoneIdAsync(ct), clock.GetUtcNow().UtcDateTime);
}
