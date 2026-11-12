using System.Diagnostics;
using ChefOS.Infrastructure.Notifications;

namespace ChefOS.Tests.Infrastructure;

public class EmailDispatchSignalTests
{
    [Fact]
    public async Task WaitReturnsOnNotify_AndRepeatedNotifiesDoNotThrow()
    {
        var signal = new EmailDispatchSignal();
        signal.Notify();
        signal.Notify();
        var watch = Stopwatch.StartNew();

        await signal.WaitAsync(TimeSpan.FromSeconds(10), default);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        await signal.WaitAsync(TimeSpan.FromMilliseconds(50), default); // plus de signal en attente : rend la main au délai
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(TimeSpan.FromSeconds(10), new CancellationToken(true)));
    }
}
