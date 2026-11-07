namespace ChefOS.Core.Vitrine;

public static class CancellationPolicy
{
    /// <summary>Annulation gratuite tant que <c>utcNow &lt;= startsAtUtc − freeCancellationHours</c> (borne incluse).</summary>
    public static bool IsFreeCancellation(DateTime startsAtUtc, DateTime utcNow, int freeCancellationHours)
        => utcNow <= startsAtUtc.AddHours(-freeCancellationHours);
}
