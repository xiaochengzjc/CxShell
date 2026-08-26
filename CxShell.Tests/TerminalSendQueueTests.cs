using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class TerminalSendQueueTests
{
    [Fact]
    public void SynchronousWritesAreProcessedInFifoOrder()
    {
        using var queue = new TerminalSendQueue();
        var values = new List<int>();

        Assert.True(queue.TryEnqueue(() => values.Add(1)));
        Assert.True(queue.TryEnqueue(() => values.Add(2)));
        Assert.True(queue.TryEnqueue(() => values.Add(3)));

        Assert.Equal(new[] { 1, 2, 3 }, values);
    }

    [Fact]
    public async Task AsyncOperationBlocksLaterWritesUntilItCompletes()
    {
        using var queue = new TerminalSendQueue();
        var values = new List<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Assert.True(queue.TryEnqueue(async cancellationToken =>
        {
            values.Add("first-start");
            started.Set();
            await Task.Run(release.Wait, cancellationToken);
            values.Add("first-end");
        }));

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        var secondWrite = Task.Run(() => queue.TryEnqueue(() => values.Add("second")));
        await Task.Delay(100);
        Assert.False(secondWrite.IsCompleted);
        Assert.Equal(new[] { "first-start" }, values);

        release.Set();
        var completed = await Task.WhenAny(secondWrite, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(secondWrite, completed);
        Assert.True(await secondWrite);
        Assert.Equal(new[] { "first-start", "first-end", "second" }, values);
    }

    [Fact]
    public void DisposedQueueRejectsNewWrites()
    {
        var queue = new TerminalSendQueue();
        queue.Dispose();

        Assert.False(queue.TryEnqueue(() => { }));
    }

    [Fact]
    public async Task FailedAsyncOperationDoesNotStopLaterWrites()
    {
        using var queue = new TerminalSendQueue();
        var values = new List<int>();

        Assert.True(queue.TryEnqueue(_ =>
            Task.FromException(new InvalidOperationException("expected"))));
        Assert.True(queue.TryEnqueue(() => values.Add(1)));

        await Task.Delay(50);
        Assert.Equal(new[] { 1 }, values);
    }
}
