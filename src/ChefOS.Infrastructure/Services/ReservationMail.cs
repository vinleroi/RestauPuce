using ChefOS.Core.Restaurants;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Mise en file des e-mails d'une réservation par <see cref="ReservationService"/> et <see cref="PaymentService"/>, dans la transaction de l'événement
/// (création, paiement, annulation) : le message n'existe que si l'événement est enregistré. Dates et heures dans le fuseau du restaurant.
/// Aucun e-mail sans adresse (réservation manuelle prise sans e-mail).
/// </summary>
internal static class ReservationMail
{
    /// <summary>Le contexte des gabarits à partir d'entités déjà lues (ou pas encore enregistrées) ; null si le client n'a pas d'adresse.</summary>
    public static ReservationEmailContext? Context(Restaurant restaurant, VitrineProfile? profile, Reservation reservation, string baseUrl, DateTime nowUtc,
        decimal amountPaid = 0m, decimal refundAmount = 0m)
    {
        if (string.IsNullOrWhiteSpace(reservation.CustomerEmail))
            return null;

        var (date, time) = RestaurantClock.ToLocal(reservation.StartsAtUtc, restaurant.TimeZoneId);
        var deadline = RestaurantClock.ToLocal(reservation.FreeCancellationUntilUtc, restaurant.TimeZoneId);
        return new ReservationEmailContext(
            restaurant.Name, profile?.AccentColor, profile?.PublicEmail, profile?.Phone, Address(restaurant.Address, profile?.City), restaurant.Slug, baseUrl,
            reservation.CustomerName, reservation.CustomerEmail, reservation.PublicToken, date, time, reservation.ServiceName, reservation.PartySize,
            amountPaid, refundAmount, deadline.Date.ToDateTime(deadline.Time), restaurant.Id,
            FreeCancellationOver: nowUtc > reservation.FreeCancellationUntilUtc);
    }

    /// <summary>Relit la réservation, son restaurant et sa vitrine dans le contexte (et la transaction) de l'appelant ; null si la réservation n'a pas d'adresse.</summary>
    public static async Task<ReservationEmailContext?> LoadAsync(ChefOsDbContext db, int reservationId, string baseUrl, DateTime nowUtc, decimal amountPaid, decimal refundAmount,
        CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null || string.IsNullOrWhiteSpace(reservation.CustomerEmail))
            return null;

        var restaurant = await db.Restaurants.AsNoTracking().FirstAsync(r => r.Id == reservation.RestaurantId, ct);
        var profile = await db.VitrineProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.RestaurantId == reservation.RestaurantId, ct);
        return Context(restaurant, profile, reservation, baseUrl, nowUtc, amountPaid, refundAmount);
    }

    /// <summary>Ajoute le message au contexte, sans SaveChanges ; vrai si un message a été ajouté (l'appelant enregistre, valide, puis réveille le dispatcher).</summary>
    public static bool Enqueue(ChefOsDbContext db, ReservationEmailContext? context, Func<ReservationEmailContext, EmailMessage> template, DateTime nowUtc) =>
        context is not null && EmailOutbox.Enqueue(db, template(context), nowUtc);

    /// <summary>L'adresse affichée par la vitrine : la rue, puis la ville si elle n'y figure pas déjà.</summary>
    private static string? Address(string? street, string? city)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(street))
            parts.Add(street.Trim());
        if (!string.IsNullOrWhiteSpace(city) && street?.Contains(city.Trim(), StringComparison.OrdinalIgnoreCase) != true)
            parts.Add(city.Trim());
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
