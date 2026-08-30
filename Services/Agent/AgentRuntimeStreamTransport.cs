using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

/// <summary>
/// Client-side transport for a caller-owned Runtime frame stream. It keeps
/// framing, response correlation, and event delivery in one place for internal
/// Runtime transport scenarios.
/// </summary>
public sealed class AgentRuntimeStreamTransport :
    IAgentRuntimeTransport,
    IAgentRuntimeEventTransport,
    IAgentRuntimeRequestCancellationTransport,
    IAsyncDisposable
{
    public const int MaximumPendingRequests = 64;
    private const int ReadBufferSize = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<
        string,
        TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);
    private readonly object _observerGate = new();
    private readonly object _pendingGate = new();
    private readonly List<Action<AgentRuntimeEventEnvelope>> _observers = [];
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _readerTask;
    private Exception? _terminalException;
    private long _generatedCancellationRequestId;
    private int _disposed;

    public AgentRuntimeStreamTransport(Stream stream, bool leaveOpen = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("Runtime stream must support reading and writing.", nameof(stream));

        _leaveOpen = leaveOpen;
        _readerTask = ReadLoopAsync();
    }

    public async Task<string> SendAsync(
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            throw new ArgumentException("Runtime request JSON is required.", nameof(requestJson));
        if (requestJson.Length > AgentRuntimeContract.MaximumJsonRequestCharacters)
        {
            throw new AgentRuntimeProtocolException(
                $"Runtime request JSON cannot exceed {AgentRuntimeContract.MaximumJsonRequestCharacters} characters.");
        }

        ThrowIfUnavailable();
        var requestId = ReadRequestId(requestJson);
        var pending = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            if (_pending.Count >= MaximumPendingRequests)
            {
                throw new AgentRuntimeProtocolException(
                    $"The Runtime client cannot have more than {MaximumPendingRequests} pending requests.");
            }

            if (!_pending.TryAdd(requestId, pending))
            {
                throw new AgentRuntimeProtocolException(
                    $"A Runtime request with ID '{requestId}' is already in progress.");
            }
        }

        try
        {
            var frame = AgentRuntimeFrameCodec.Encode(requestJson);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                await _stream.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    public async Task RequestCancellationAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequestId = requestId?.Trim() ?? string.Empty;
        if (normalizedRequestId.Length == 0)
            throw new ArgumentException("A target request ID is required.", nameof(requestId));
        if (normalizedRequestId.Length > AgentRuntimeContract.MaximumRequestIdCharacters)
        {
            throw new ArgumentException(
                $"Target request ID cannot exceed {AgentRuntimeContract.MaximumRequestIdCharacters} characters.",
                nameof(requestId));
        }

        var sequence = Interlocked.Increment(ref _generatedCancellationRequestId);
        var cancellationRequest = new RuntimeRequestEnvelope(
            $"cxshell-cancel-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{sequence}",
            AgentRuntimeMethodNames.RequestCancel,
            JsonSerializer.SerializeToElement(new { requestId = normalizedRequestId }, JsonOptions));
        var requestJson = JsonSerializer.Serialize(cancellationRequest, JsonOptions);
        await SendAsync(requestJson, cancellationToken).ConfigureAwait(false);
    }

    public IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ThrowIfUnavailable();
        lock (_observerGate)
            _observers.Add(observer);
        return new EventSubscription(this, observer);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var disposedException = new ObjectDisposedException(nameof(AgentRuntimeStreamTransport));
        FailPending(disposedException);
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The reader has already completed all pending requests with its error.
        }

        if (!_leaveOpen)
            await _stream.DisposeAsync().ConfigureAwait(false);

        _writeGate.Dispose();
        _lifetimeCts.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var reader = new AgentRuntimeFrameReader();
        var buffer = GC.AllocateUninitializedArray<byte>(ReadBufferSize);

        try
        {
            while (true)
            {
                var bytesRead = await _stream.ReadAsync(
                        buffer.AsMemory(),
                        _lifetimeCts.Token)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    if (reader.BufferedBytes > 0)
                    {
                        throw new EndOfStreamException(
                            "Runtime stream ended in the middle of a frame.");
                    }

                    throw new EndOfStreamException("Runtime stream closed before all responses arrived.");
                }

                reader.Append(buffer.AsSpan(0, bytesRead));
                while (reader.TryReadJson(out var json))
                    ProcessFrame(json!);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var failure = exception is AgentRuntimeProtocolException or EndOfStreamException
                ? exception
                : new AgentRuntimeProtocolException(
                    "Runtime stream returned an invalid frame.",
                    exception);
            Interlocked.CompareExchange(ref _terminalException, failure, null);
            FailPending(failure);
        }
    }

    private void ProcessFrame(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new AgentRuntimeProtocolException("Runtime frame JSON must be an object.");

        var root = document.RootElement;
        if (root.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "event", StringComparison.Ordinal))
        {
            var @event = JsonSerializer.Deserialize<AgentRuntimeEventEnvelope>(json, JsonOptions)
                ?? throw new AgentRuntimeProtocolException("Runtime event frame is invalid.");
            PublishEvent(@event);
            return;
        }

        if (!root.TryGetProperty("requestId", out var requestId) ||
            requestId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(requestId.GetString()))
        {
            throw new AgentRuntimeProtocolException(
                "Runtime response frame does not contain a request ID.");
        }

        if (_pending.TryRemove(requestId.GetString()!, out var pending))
            pending.TrySetResult(json);
    }

    private void PublishEvent(AgentRuntimeEventEnvelope @event)
    {
        Action<AgentRuntimeEventEnvelope>[] observers;
        lock (_observerGate)
            observers = _observers.ToArray();

        foreach (var observer in observers)
        {
            try
            {
                observer(@event);
            }
            catch
            {
                // One event consumer must not stop the Runtime reader.
            }
        }
    }

    private static string ReadRequestId(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("requestId", out var requestId) ||
                requestId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(requestId.GetString()))
            {
                throw new AgentRuntimeProtocolException(
                    "Runtime request JSON must contain a requestId.");
            }

            var normalized = requestId.GetString()!.Trim();
            if (normalized.Length > AgentRuntimeContract.MaximumRequestIdCharacters)
            {
                throw new AgentRuntimeProtocolException(
                    $"Request ID cannot exceed {AgentRuntimeContract.MaximumRequestIdCharacters} characters.");
            }

            return normalized;
        }
        catch (JsonException exception)
        {
            throw new AgentRuntimeProtocolException(
                "Runtime request JSON is invalid.",
                exception);
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
                pending.TrySetException(exception);
        }
    }

    private void UnsubscribeEvents(Action<AgentRuntimeEventEnvelope> observer)
    {
        lock (_observerGate)
            _observers.Remove(observer);
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AgentRuntimeStreamTransport));

        var terminalException = Volatile.Read(ref _terminalException);
        if (terminalException != null)
            throw terminalException;
    }

    private sealed class EventSubscription : IDisposable
    {
        private AgentRuntimeStreamTransport? _owner;
        private readonly Action<AgentRuntimeEventEnvelope> _observer;

        public EventSubscription(
            AgentRuntimeStreamTransport owner,
            Action<AgentRuntimeEventEnvelope> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.UnsubscribeEvents(_observer);
        }
    }

    private sealed record RuntimeRequestEnvelope(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] JsonElement Parameters);
}
