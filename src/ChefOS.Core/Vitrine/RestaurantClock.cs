namespace ChefOS.Core.Vitrine;

/// <summary>Conversions entre heure locale du restaurant et UTC, selon un fuseau IANA (ex. « Europe/Brussels »).</summary>
public static class RestaurantClock
{
    /// <summary>
    /// Heure locale → UTC. Une heure inexistante (saut de printemps) est décalée vers l'avant de la durée du saut ;
    /// une heure ambiguë (retour d'automne) prend la première occurrence (heure d'été).
    /// </summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local))
        {
            var before = tz.GetUtcOffset(local.AddHours(-3));
            var after = tz.GetUtcOffset(local.AddHours(3));
            local = local.Add(after - before);
        }

        var offset = tz.IsAmbiguousTime(local)
            ? tz.GetAmbiguousTimeOffsets(local).Max()
            : tz.GetUtcOffset(local);

        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>UTC → date et heure locales du restaurant.</summary>
    public static (DateOnly Date, TimeOnly Time) ToLocal(DateTime utc, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return (DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    /// <summary>Date locale du jour au moment <paramref name="utcNow"/>.</summary>
    public static DateOnly Today(DateTime utcNow, string timeZoneId) => ToLocal(utcNow, timeZoneId).Date;
}
