using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Service d'arrière-plan des deux sites : envoie la file <c>OutgoingEmails</c>. Il se réveille toutes les 5 s, ou tout de suite sur
/// <see cref="EmailDispatchSignal.Notify"/>, et purge au plus une fois par heure les lignes de plus de 30 jours.
/// <para>
/// Réservation atomique : chaque message dû est pris par un <c>UPDATE … FOR UPDATE SKIP LOCKED … RETURNING</c> qui pose un bail de 2 min.
/// Deux processus ne prennent jamais le même message ; un message réservé par un processus arrêté repart à l'expiration du bail.
/// Un message à la fois : l'envoi est borné à 30 s, donc toujours terminé avant la fin du bail.
/// </para>
/// <para>
/// Nouvelles tentatives : échec <see cref="EmailFailure.Temporary"/> → nouvel essai après 1 min, 5 min, 30 min, 2 h puis 6 h, abandon au 6e échec ;
/// <see cref="EmailFailure.Permanent"/> → abandon immédiat ; <see cref="EmailFailure.ServerUnavailable"/> (ou délai de 30 s dépassé) → le passage
/// s'arrête, le message est remis tel quel (aucun essai compté, bail levé) et le serveur n'est retenté que 30 s plus tard. Un message créé il y a plus de
/// 24 h est abandonné sans envoi. Le texte et le HTML sont effacés à l'envoi ou à l'abandon.
/// </para>
/// Journaux : identifiant, type, destinataire masqué et sujet ; jamais le corps. Une erreur de boucle (base injoignable…) est journalisée et la boucle continue.
/// </summary>
public sealed class OutgoingEmailDispatcher(
    IDbContextFactory<ChefOsDbContext> factory, IEmailTransport transport, EmailDispatchSignal signal, TimeProvider clock,
    ILogger<OutgoingEmailDispatcher> logger) : BackgroundService
{
    internal static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ServerRetryDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private const int LastErrorMaxLength = 500;

    private DateTime _serverRetryAt = DateTime.MinValue;
    private DateTime? _lastPurge;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // ne pas retarder le démarrage de l'hôte

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = PollInterval;
            try
            {
                if (_lastPurge is not { } last || Now - last >= PurgeInterval)
                {
                    _lastPurge = Now;
                    await PurgeAsync(stoppingToken);
                }
                await DispatchDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Base injoignable ou non migrée… : on journalise, on espace les essais, et la boucle continue (le site ne doit pas s'arrêter).
                logger.LogError(ex, "File d'e-mails : passage interrompu, nouvel essai dans {Delay} s", ServerRetryDelay.TotalSeconds);
                wait = ServerRetryDelay;
            }

            try
            {
                await signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Un passage : réserve, envoie et marque les messages dus, un par un ; renvoie le nombre de messages traités (envoyés, reportés ou abandonnés).</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        if (Now < _serverRetryAt)
            return 0;

        var processed = 0;
        while (await ClaimAsync(ct) is { } email)
        {
            if (!await ProcessAsync(email, ct))
                break;
            processed++;
        }
        return processed;
    }

    /// <summary>Supprime les lignes créées il y a plus de 30 jours, quel que soit leur statut ; renvoie le nombre supprimé.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var limit = Now - Retention;
        await using var db = await factory.CreateDbContextAsync(ct);
        var deleted = await db.OutgoingEmails.Where(e => e.CreatedAt < limit).ExecuteDeleteAsync(ct);
        if (deleted > 0)
            logger.LogInformation("File d'e-mails : {Count} ligne(s) de plus de 30 jours supprimée(s)", deleted);
        return deleted;
    }

    /// <summary>Réserve le plus ancien message dû et libre (bail absent ou expiré), en une instruction : les lignes verrouillées par un autre processus sont sautées.</summary>
    private async Task<OutgoingEmail?> ClaimAsync(CancellationToken ct)
    {
        var now = Now;
        var lease = now + Lease;
        var pending = nameof(OutgoingEmailStatus.Pending);
        await using var db = await factory.CreateDbContextAsync(ct);
        var claimed = await db.OutgoingEmails.FromSql($"""
            UPDATE "OutgoingEmails" SET "LockedUntil" = {lease}
            WHERE "Id" = (
                SELECT "Id" FROM "OutgoingEmails"
                WHERE "Status" = {pending} AND "NextAttemptAt" <= {now} AND ("LockedUntil" IS NULL OR "LockedUntil" <= {now})
                ORDER BY "NextAttemptAt", "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
        return claimed.SingleOrDefault();
    }

    /// <summary>Envoie un message réservé et enregistre le résultat. Renvoie faux si le serveur est indisponible : le passage s'arrête.</summary>
    private async Task<bool> ProcessAsync(OutgoingEmail email, CancellationToken ct)
    {
        var to = LoggingEmailTransport.Mask(email.To);
        if (Now - email.CreatedAt > MaxAge)
        {
            logger.LogError("E-mail {Id} ({Kind}) à {To} abandonné sans envoi : en attente depuis plus de 24 h ({Subject})", email.Id, email.Kind, to, email.Subject);
            await AbandonAsync(email, email.Attempts, "Abandonné : non envoyé dans les 24 h");
            return true;
        }

        EmailFailure? failure = null;
        var reason = string.Empty;
        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await transport.SendAsync(ToMessage(email), linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arrêt du site pendant l'envoi : le message est remis tel quel pour le prochain démarrage (ou un autre processus).
            await ReleaseAsync(email.Id);
            throw;
        }
        catch (OperationCanceledException)
        {
            (failure, reason) = (EmailFailure.ServerUnavailable, $"délai de {SendTimeout.TotalSeconds} s dépassé");
        }
        catch (EmailTransportException ex)
        {
            (failure, reason) = (ex.Failure, ex.Message);
        }
        catch (Exception ex)
        {
            // Erreur inattendue du transport : comptée comme temporaire, pour qu'un message qui la provoque finisse abandonné sans bloquer la file.
            (failure, reason) = (EmailFailure.Temporary, $"erreur inattendue ({ex.GetType().Name})");
        }

        if (failure is null)
        {
            // Envoyé : hors du bloc d'envoi (une erreur d'écriture ici ne doit pas passer pour un échec d'envoi), et enregistré même si l'arrêt
            // du site est demandé entre-temps. Si l'écriture échoue, le bail expire et le message repart : au moins une fois, jamais perdu.
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            await db.OutgoingEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(set => set
                .SetProperty(e => e.Status, OutgoingEmailStatus.Sent)
                .SetProperty(e => e.SentAt, Now)
                .SetProperty(e => e.Attempts, email.Attempts + 1)
                .SetProperty(e => e.TextBody, (string?)null)
                .SetProperty(e => e.HtmlBody, (string?)null)
                .SetProperty(e => e.LockedUntil, (DateTime?)null), CancellationToken.None);
            logger.LogInformation("E-mail {Id} ({Kind}) envoyé à {To} : {Subject}", email.Id, email.Kind, to, email.Subject);
            return true;
        }

        var error = Truncate($"{failure} : {reason}");
        switch (failure)
        {
            case EmailFailure.ServerUnavailable:
                _serverRetryAt = Now + ServerRetryDelay;
                await ReleaseAsync(email.Id);
                logger.LogWarning("Serveur d'e-mails indisponible ({Reason}) : e-mail {Id} remis en file, nouvel essai dans {Delay} s",
                    reason, email.Id, ServerRetryDelay.TotalSeconds);
                return false;

            case EmailFailure.Temporary when email.Attempts + 1 <= RetryDelays.Length:
                var next = Now + RetryDelays[email.Attempts];
                await using (var db = await factory.CreateDbContextAsync(CancellationToken.None))
                {
                    await db.OutgoingEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(set => set
                        .SetProperty(e => e.Attempts, email.Attempts + 1)
                        .SetProperty(e => e.NextAttemptAt, next)
                        .SetProperty(e => e.LastError, error)
                        .SetProperty(e => e.LockedUntil, (DateTime?)null), CancellationToken.None);
                }
                logger.LogWarning("E-mail {Id} ({Kind}) à {To} refusé temporairement ({Reason}) : essai {Attempt}, nouvel essai à {Next:u} ({Subject})",
                    email.Id, email.Kind, to, reason, email.Attempts + 1, next, email.Subject);
                return true;

            case EmailFailure.Temporary:
                logger.LogError("E-mail {Id} ({Kind}) à {To} abandonné après {Attempts} essais ({Reason}) : {Subject}",
                    email.Id, email.Kind, to, email.Attempts + 1, reason, email.Subject);
                await AbandonAsync(email, email.Attempts + 1, error);
                return true;

            default:
                logger.LogWarning("E-mail {Id} ({Kind}) à {To} refusé définitivement ({Reason}) : abandonné ({Subject})",
                    email.Id, email.Kind, to, reason, email.Subject);
                await AbandonAsync(email, email.Attempts + 1, error);
                return true;
        }
    }

    private async Task AbandonAsync(OutgoingEmail email, int attempts, string error)
    {
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        await db.OutgoingEmails.Where(e => e.Id == email.Id).ExecuteUpdateAsync(set => set
            .SetProperty(e => e.Status, OutgoingEmailStatus.Abandoned)
            .SetProperty(e => e.Attempts, attempts)
            .SetProperty(e => e.LastError, Truncate(error))
            .SetProperty(e => e.TextBody, (string?)null)
            .SetProperty(e => e.HtmlBody, (string?)null)
            .SetProperty(e => e.LockedUntil, (DateTime?)null), CancellationToken.None);
    }

    /// <summary>Lève le bail sans rien changer d'autre : le message repart au prochain passage.</summary>
    private async Task ReleaseAsync(long id)
    {
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        await db.OutgoingEmails.Where(e => e.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.LockedUntil, (DateTime?)null), CancellationToken.None);
    }

    private static EmailMessage ToMessage(OutgoingEmail email) =>
        new(email.Kind, email.To, email.Subject, email.TextBody ?? string.Empty, email.HtmlBody ?? string.Empty, email.ReplyTo, email.RestaurantId);

    private static string Truncate(string value) => value.Length > LastErrorMaxLength ? value[..LastErrorMaxLength] : value;
}
