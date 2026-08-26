using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CxShell.Services;

/// <summary>
/// Serializes all terminal writes for one connection generation.
/// </summary>
public sealed class TerminalSendQueue : IDisposable
{
    private sealed record SendOperation(
        Func<CancellationToken, Task> Action,
        TaskCompletionSource<bool> Completion);

    private readonly Channel<SendOperation> _channel =
        Channel.CreateUnbounded<SendOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private int _disposed;

    public TerminalSendQueue()
    {
        _worker = Task.Run(ProcessAsync);
    }

    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryEnqueue(
            _ =>
            {
                action();
                return Task.CompletedTask;
            },
            waitForCompletion: true);
    }

    public bool TryEnqueue(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryEnqueue(action, waitForCompletion: false);
    }

    private bool TryEnqueue(Func<CancellationToken, Task> action, bool waitForCompletion)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new SendOperation(action, completion)))
            return false;

        if (!waitForCompletion)
        {
            ObserveCompletion(completion.Task);
            return true;
        }

        try
        {
            completion.Task.GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Terminal send failed: {ex.Message}");
            return false;
        }
    }

    private static void ObserveCompletion(Task completion)
    {
        _ = completion.ContinueWith(
            task =>
            {
                if (task.Exception != null)
                    Debug.WriteLine($"Terminal async send failed: {task.Exception.GetBaseException().Message}");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var operation in _channel.Reader.ReadAllAsync(_cancellation.Token))
            {
                try
                {
                    await operation.Action(_cancellation.Token).ConfigureAwait(false);
                    operation.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    operation.Completion.TrySetCanceled(_cancellation.Token);
                }
                catch (Exception ex)
                {
                    operation.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            while (_channel.Reader.TryRead(out var pending))
                pending.Completion.TrySetCanceled(_cancellation.Token);
        }
        catch (Exception ex)
        {
            while (_channel.Reader.TryRead(out var pending))
                pending.Completion.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.TryComplete();
        _cancellation.Cancel();
        if (Task.CurrentId != _worker.Id)
        {
            try
            {
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Connection teardown must remain best-effort.
            }
        }

        _cancellation.Dispose();
    }
}
