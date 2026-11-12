namespace ChefOS.Infrastructure.Services;

/// <summary>Normalisation des étiquettes de jour (dates saisies) : minuit UTC. Les jours « locaux » (aujourd'hui, futur, bornes) relèvent de <see cref="RestaurantCalendar"/>.</summary>
internal static class UtcDays
{
    /// <summary>Minuit UTC du jour de <paramref name="value"/> ; une date sans fuseau est lue comme étant en UTC.</summary>
    public static DateTime Of(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
    }
}
