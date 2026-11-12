namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Réveil du dispatcher du même processus (singleton) : un service qui vient d'ajouter un e-mail appelle <see cref="Notify"/> après son commit,
/// et le message part sans attendre le réveil périodique. Plusieurs appels avant le réveil n'en font qu'un.
/// </summary>
public sealed class EmailDispatchSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Un réveil est déjà en attente.
        }
    }

    /// <summary>Rend la main au premier <see cref="Notify"/> (même antérieur à l'appel) ou au bout de <paramref name="timeout"/>.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}
