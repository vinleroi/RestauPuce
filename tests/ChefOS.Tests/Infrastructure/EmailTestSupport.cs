using System.Collections.Concurrent;
using ChefOS.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Faux transport : compte les envois par sujet, échoue ou bloque à la demande.</summary>
public sealed class FakeEmailTransport : IEmailTransport
{
    public EmailFailure? Fail { get; set; }
    public Func<CancellationToken, Task>? Before { get; set; }
    public int Calls;
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();
    public ConcurrentDictionary<string, int> CountBySubject { get; } = new();

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Before is not null)
            await Before(ct);
        if (Fail is { } failure)
            throw new EmailTransportException("SMTP 451 (simulé)", failure);
        Sent.Enqueue(message);
        CountBySubject.AddOrUpdate(message.Subject, 1, (_, n) => n + 1);
    }
}

/// <summary>Journal qui garde ses entrées (niveau et message formaté, exception comprise), sans risque entre fils.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    public List<(LogLevel Level, string Message)> Entries { get { lock (_entries) return [.. _entries]; } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
            _entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception)));
    }
}
