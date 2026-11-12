using System.Diagnostics;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// File d'e-mails contre PostgreSQL : réservation atomique, nouvelles tentatives, panne du serveur, délai d'envoi, purge.
/// L'horloge est figée (<see cref="FakeTimeProvider"/>) ; le transport est un faux en mémoire.
/// </summary>
[Collection("postgres")]
public class OutgoingEmailDispatcherTests(PostgresFixture postgres)
{
    private const string To = "jean@client.test";
    private const string Body = "Bonjour Jean, votre table est confirmée.";

    private sealed record Setup(DbContextOptions<ChefOsDbContext> Options, FakeTimeProvider Clock, FakeEmailTransport Transport, OutgoingEmailDispatcher Dispatcher)
    {
        public DateTime Now => Clock.GetUtcNow().UtcDateTime;
    }

    private async Task<Setup> CreateAsync(ILogger<OutgoingEmailDispatcher>? logger = null)
    {
        var options = await postgres.CreateDatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var transport = new FakeEmailTransport();
        return new Setup(options, clock, transport, Dispatcher(options, transport, clock, logger: logger));
    }

    private static OutgoingEmailDispatcher Dispatcher(DbContextOptions<ChefOsDbContext> options, IEmailTransport transport, TimeProvider clock,
        EmailDispatchSignal? signal = null, ILogger<OutgoingEmailDispatcher>? logger = null) =>
        new(new TestContextFactory(options, null), transport, signal ?? new EmailDispatchSignal(), clock, logger ?? NullLogger<OutgoingEmailDispatcher>.Instance);

    private static EmailMessage Message(string subject = "Votre réservation est confirmée", string to = To) =>
        new(EmailKind.ReservationConfirmed, to, subject, Body, "<p>" + Body + "</p>", "bistro@resto.test", 7);

    private static async Task EnqueueAsync(DbContextOptions<ChefOsDbContext> options, DateTime now, params EmailMessage[] messages)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        foreach (var message in messages)
            EmailOutbox.Enqueue(db, message, now);
        await db.SaveChangesAsync();
    }

    private static async Task<List<OutgoingEmail>> RowsAsync(DbContextOptions<ChefOsDbContext> options)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        return await db.OutgoingEmails.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private static async Task SetAsync(DbContextOptions<ChefOsDbContext> options, Func<OutgoingEmail, bool> which, Action<OutgoingEmail> change)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        foreach (var row in (await db.OutgoingEmails.ToListAsync()).Where(which))
            change(row);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DueMessage_IsSentOnce_AndItsBodyIsCleared()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now, Message());

        Assert.Equal(1, await s.Dispatcher.DispatchDueAsync(default));
        Assert.Equal(0, await s.Dispatcher.DispatchDueAsync(default));

        var row = Assert.Single(await RowsAsync(s.Options));
        Assert.Equal(OutgoingEmailStatus.Sent, row.Status);
        Assert.Equal(s.Now, row.SentAt);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.TextBody);
        Assert.Null(row.HtmlBody);
        Assert.Null(row.LockedUntil);
        Assert.Equal((EmailKind.ReservationConfirmed, 7, To), (row.Kind, row.RestaurantId, row.To));

        var sent = Assert.Single(s.Transport.Sent);
        Assert.Equal(Message(), sent);
    }

    [Fact]
    public async Task TemporaryFailure_RetriesAfterTheScheduledDelays()
    {
        var s = await CreateAsync();
        s.Transport.Fail = EmailFailure.Temporary;
        await EnqueueAsync(s.Options, s.Now, Message());

        foreach (var minutes in new[] { 1, 5, 30, 120, 360 })
        {
            Assert.Equal(1, await s.Dispatcher.DispatchDueAsync(default));
            var row = Assert.Single(await RowsAsync(s.Options));
            Assert.Equal(OutgoingEmailStatus.Pending, row.Status);
            Assert.Equal(TimeSpan.FromMinutes(minutes), row.NextAttemptAt - s.Now);
            Assert.NotNull(row.TextBody);
            Assert.NotNull(row.LastError);
            Assert.Null(row.LockedUntil);

            // Pas encore dû une seconde avant l'échéance.
            s.Clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await s.Dispatcher.DispatchDueAsync(default));
            s.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, await s.Dispatcher.DispatchDueAsync(default));
        var abandoned = Assert.Single(await RowsAsync(s.Options));
        Assert.Equal(OutgoingEmailStatus.Abandoned, abandoned.Status);
        Assert.Equal(6, abandoned.Attempts);
        Assert.Equal(6, s.Transport.Calls);
        Assert.Null(abandoned.TextBody);
        Assert.Null(abandoned.HtmlBody);
    }

    [Fact]
    public async Task PermanentFailure_AbandonsImmediately()
    {
        var s = await CreateAsync();
        s.Transport.Fail = EmailFailure.Permanent;
        await EnqueueAsync(s.Options, s.Now, Message());

        Assert.Equal(1, await s.Dispatcher.DispatchDueAsync(default));

        var row = Assert.Single(await RowsAsync(s.Options));
        Assert.Equal(OutgoingEmailStatus.Abandoned, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.TextBody);
        Assert.Null(row.HtmlBody);
        Assert.False(string.IsNullOrWhiteSpace(row.LastError));
        Assert.DoesNotContain("@", row.LastError);
        Assert.DoesNotContain(Body, row.LastError);
    }

    [Fact]
    public async Task ServerUnavailable_StopsThePassWithoutCountingAnAttempt()
    {
        var s = await CreateAsync();
        s.Transport.Fail = EmailFailure.ServerUnavailable;
        await EnqueueAsync(s.Options, s.Now, Message("A"), Message("B"), Message("C"));

        Assert.Equal(0, await s.Dispatcher.DispatchDueAsync(default));

        Assert.Equal(1, s.Transport.Calls);
        Assert.All(await RowsAsync(s.Options), row =>
        {
            Assert.Equal(OutgoingEmailStatus.Pending, row.Status);
            Assert.Equal(0, row.Attempts);
            Assert.Null(row.LockedUntil);
            Assert.Equal(s.Now, row.NextAttemptAt);
            Assert.NotNull(row.TextBody);
        });
    }

    [Fact]
    public async Task ServerBack_SendsTheQueueOnTheNextPass()
    {
        var s = await CreateAsync();
        s.Transport.Fail = EmailFailure.ServerUnavailable;
        await EnqueueAsync(s.Options, s.Now, Message("A"), Message("B"), Message("C"));
        await s.Dispatcher.DispatchDueAsync(default);

        s.Transport.Fail = null;
        // Le serveur n'est retenté que 30 s plus tard.
        s.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await s.Dispatcher.DispatchDueAsync(default));
        Assert.Equal(1, s.Transport.Calls);

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, await s.Dispatcher.DispatchDueAsync(default));

        Assert.Equal(["A", "B", "C"], s.Transport.Sent.Select(m => m.Subject).Order());
        Assert.All(await RowsAsync(s.Options), row => Assert.Equal((OutgoingEmailStatus.Sent, 1), (row.Status, row.Attempts)));
    }

    [Fact]
    public async Task MessageOlderThan24Hours_IsAbandonedWithoutSending()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now - TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), Message("Trop vieux"));
        await EnqueueAsync(s.Options, s.Now - TimeSpan.FromHours(23), Message("Encore valable"));

        Assert.Equal(2, await s.Dispatcher.DispatchDueAsync(default));

        Assert.Equal(["Encore valable"], s.Transport.Sent.Select(m => m.Subject));
        var rows = await RowsAsync(s.Options);
        Assert.Equal(OutgoingEmailStatus.Abandoned, rows[0].Status);
        Assert.Equal(0, rows[0].Attempts);
        Assert.Null(rows[0].TextBody);
        Assert.Null(rows[0].HtmlBody);
        Assert.NotNull(rows[0].LastError);
        Assert.Equal(OutgoingEmailStatus.Sent, rows[1].Status);
    }

    [Fact]
    public async Task MessageNotYetDue_IsLeftAlone()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now, Message());
        await SetAsync(s.Options, _ => true, row => row.NextAttemptAt = s.Now.AddMinutes(1));

        Assert.Equal(0, await s.Dispatcher.DispatchDueAsync(default));

        Assert.Equal(0, s.Transport.Calls);
        var row = Assert.Single(await RowsAsync(s.Options));
        Assert.Equal((OutgoingEmailStatus.Pending, 0), (row.Status, row.Attempts));
        Assert.Null(row.LockedUntil);
    }

    [Fact]
    public async Task TwoDispatchers_NeverSendTheSameMessageTwice()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now, Enumerable.Range(1, 20).Select(i => Message($"Message {i}")).ToArray());
        s.Transport.Before = ct => Task.Delay(10, ct); // les deux passages se chevauchent
        var other = Dispatcher(s.Options, s.Transport, s.Clock);

        var processed = await Task.WhenAll(s.Dispatcher.DispatchDueAsync(default), other.DispatchDueAsync(default));

        Assert.Equal(20, processed.Sum());
        Assert.Equal(20, s.Transport.CountBySubject.Count);
        Assert.All(s.Transport.CountBySubject, pair => Assert.Equal(1, pair.Value));
        Assert.All(await RowsAsync(s.Options), row => Assert.Equal((OutgoingEmailStatus.Sent, 1), (row.Status, row.Attempts)));
    }

    [Fact]
    public async Task ExpiredLease_IsPickedUpAgain()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now, Message());
        // Réservé par un processus arrêté pendant l'envoi : son bail a expiré il y a 1 s.
        await SetAsync(s.Options, _ => true, row => row.LockedUntil = s.Now.AddSeconds(-1));

        Assert.Equal(1, await s.Dispatcher.DispatchDueAsync(default));

        Assert.Single(s.Transport.Sent);
        Assert.Equal(OutgoingEmailStatus.Sent, Assert.Single(await RowsAsync(s.Options)).Status);
    }

    [Fact]
    public async Task ActiveLease_IsLeftAlone()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now, Message());
        await SetAsync(s.Options, _ => true, row => row.LockedUntil = s.Now.AddMinutes(1));

        Assert.Equal(0, await s.Dispatcher.DispatchDueAsync(default));

        Assert.Equal(0, s.Transport.Calls);
    }

    [Fact]
    public async Task HangingTransport_TimesOutAsServerUnavailable()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now, Message());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Transport.Before = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var watch = Stopwatch.StartNew();

        var pass = s.Dispatcher.DispatchDueAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        s.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(200);
        Assert.False(pass.IsCompleted, "le délai d'envoi est de 30 s");
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await pass.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(35));
        var row = Assert.Single(await RowsAsync(s.Options));
        Assert.Equal((OutgoingEmailStatus.Pending, 0), (row.Status, row.Attempts));
        Assert.Null(row.LockedUntil);

        // L'arrêt du site pendant un envoi bloqué rend la main sans attendre le délai, et remet le message tel quel.
        var hosted = Dispatcher(s.Options, s.Transport, s.Clock);
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await hosted.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watch.Restart();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await hosted.StopAsync(stop.Token);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(hosted.ExecuteTask!.IsCompleted);
        row = Assert.Single(await RowsAsync(s.Options));
        Assert.Equal((OutgoingEmailStatus.Pending, 0), (row.Status, row.Attempts));
        Assert.Null(row.LockedUntil);
    }

    [Fact]
    public async Task Purge_DeletesRowsOlderThan30Days()
    {
        var s = await CreateAsync();
        await EnqueueAsync(s.Options, s.Now.AddDays(-31), Message("Vieux"));
        await EnqueueAsync(s.Options, s.Now.AddDays(-29), Message("Récent"));
        await SetAsync(s.Options, _ => true, row => row.Status = OutgoingEmailStatus.Sent);

        Assert.Equal(1, await s.Dispatcher.PurgeAsync(default));

        Assert.Equal("Récent", Assert.Single(await RowsAsync(s.Options)).Subject);
    }

    [Fact]
    public async Task Notify_WakesTheLoopWithoutWaitingForThePollInterval()
    {
        var s = await CreateAsync();
        var signal = new EmailDispatchSignal();
        var hosted = Dispatcher(s.Options, s.Transport, s.Clock, signal);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Transport.Before = _ =>
        {
            sent.TrySetResult();
            return Task.CompletedTask;
        };
        await hosted.StartAsync(default);
        await Task.Delay(500); // premier passage terminé, file vide : la boucle attend

        await EnqueueAsync(s.Options, s.Now, Message());
        signal.Notify();

        await sent.Task.WaitAsync(TimeSpan.FromSeconds(3)); // bien avant le réveil des 5 s
        await hosted.StopAsync(default);
    }

    [Fact]
    public async Task LoopError_IsLoggedAndTheLoopKeepsRunning()
    {
        var options = new DbContextOptionsBuilder<ChefOsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=absente;Username=u;Password=p;Timeout=2").Options;
        var logger = new ListLogger<OutgoingEmailDispatcher>();
        var signal = new EmailDispatchSignal();
        var hosted = Dispatcher(options, new FakeEmailTransport(), TimeProvider.System, signal, logger);

        await hosted.StartAsync(default);
        await WaitForAsync(() => logger.Entries.Count(e => e.Level == LogLevel.Error) >= 1);
        signal.Notify();
        await WaitForAsync(() => logger.Entries.Count(e => e.Level == LogLevel.Error) >= 2);

        Assert.False(hosted.ExecuteTask!.IsCompleted, "la boucle ne s'arrête pas sur une erreur");
        await hosted.StopAsync(default);
    }

    [Fact]
    public async Task Logs_MaskTheRecipient_AndNeverContainTheBody()
    {
        var logger = new ListLogger<OutgoingEmailDispatcher>();
        var s = await CreateAsync(logger);
        await EnqueueAsync(s.Options, s.Now, Message("Envoyé"));
        await s.Dispatcher.DispatchDueAsync(default);
        s.Transport.Fail = EmailFailure.Permanent;
        await EnqueueAsync(s.Options, s.Now, Message("Refusé"));
        await s.Dispatcher.DispatchDueAsync(default);

        var visible = logger.Entries.Where(e => e.Level >= LogLevel.Information).ToList();
        Assert.Contains(visible, e => e.Message.Contains("j***@c***.test") && e.Message.Contains("Envoyé"));
        Assert.Contains(visible, e => e.Level >= LogLevel.Warning && e.Message.Contains("Refusé"));
        Assert.All(visible, e =>
        {
            Assert.DoesNotContain(To, e.Message);
            Assert.DoesNotContain(Body, e.Message);
        });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition non atteinte dans les 15 s");
            await Task.Delay(50);
        }
    }
}
