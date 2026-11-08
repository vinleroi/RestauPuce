using System.Threading.RateLimiting;

namespace ChefOS.Vitrine.Support;

/// <summary>
/// Limite les tentatives sensibles (création et annulation d'une réservation, webhook) : fenêtre fixe de 10 par minute et par clé (adresse IP, circuit).
/// Singleton : les compteurs sont partagés par toutes les requêtes et tous les circuits interactifs.
/// </summary>
public sealed class AttemptLimiter : IDisposable
{
    public const int PermitLimit = 10;
    public const string Message = "Trop de tentatives, réessayez dans une minute";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit, Window = Window, QueueLimit = 0, AutoReplenishment = true
        }));

    public bool TryAcquire(string key)
    {
        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Garde d'un circuit interactif : une tentative doit passer à la fois la limite du circuit et celle de l'adresse IP du client
/// (lue au rendu initial de la page, puis conservée). Sans adresse connue, seul le circuit compte.
/// </summary>
public sealed class CircuitAttempts(AttemptLimiter limiter)
{
    private readonly string _circuit = "circuit:" + Guid.NewGuid().ToString("N");

    public bool TryAcquire(string? clientIp) =>
        limiter.TryAcquire(_circuit) && (string.IsNullOrEmpty(clientIp) || limiter.TryAcquire("ip:" + clientIp));
}
