using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace CxShell.Services.Agent;

public interface IAgentRuntimeTransport
{
    Task<string> SendAsync(
        string requestJson,
        CancellationToken cancellationToken = default);
}

public interface IAgentRuntimeEventTransport
{
    IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer);
}

public interface IAgentRuntimeRequestCancellationTransport
{
    Task RequestCancellationAsync(
        string requestId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process transport used by CxShell's own Agent Runtime. Keeping this
/// behind a transport interface keeps the Runtime boundary independently testable.
/// </summary>
public sealed class InProcessAgentRuntimeTransport : IAgentRuntimeTransport, IAgentRuntimeEventTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AgentRuntimeJsonEndpoint _endpoint;
    private readonly IAgentRuntimeHost _host;

    public InProcessAgentRuntimeTransport(IAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _endpoint = new AgentRuntimeJsonEndpoint(host);
    }

    public Task<string> SendAsync(
        string requestJson,
        CancellationToken cancellationToken = default)
        => _endpoint.DispatchAsync(requestJson, cancellationToken);

    public IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _host.Subscribe(@event => observer(new AgentRuntimeEventEnvelope(
            "event",
            @event.EventName,
            @event.Module,
            @event.RequestId,
            @event.Method,
            SerializePayload(@event.Payload))));
    }

    private static JsonElement? SerializePayload(object? payload)
        => payload == null
            ? null
            : JsonSerializer.SerializeToElement(payload, JsonOptions);
}

public interface IAgentRuntimeFrameEndpoint
{
    Task<byte[]> DispatchAsync(
        ReadOnlyMemory<byte> requestFrame,
        CancellationToken cancellationToken = default);

    Task<byte[]> DispatchJsonAsync(
        string requestJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches one complete length-prefixed frame. The endpoint does not own the
/// caller's stream and remains an internal Runtime transport abstraction.
/// </summary>
public sealed class AgentRuntimeFrameEndpoint : IAgentRuntimeFrameEndpoint
{
    private readonly AgentRuntimeJsonEndpoint _endpoint;

    public AgentRuntimeFrameEndpoint(IAgentRuntimeHost host)
    {
        _endpoint = new AgentRuntimeJsonEndpoint(host);
    }

    public async Task<byte[]> DispatchAsync(
        ReadOnlyMemory<byte> requestFrame,
        CancellationToken cancellationToken = default)
    {
        var requestJson = AgentRuntimeFrameCodec.DecodeFrame(requestFrame.Span);
        return await DispatchJsonAsync(requestJson, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> DispatchJsonAsync(
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        var responseJson = await _endpoint.DispatchAsync(requestJson, cancellationToken)
            .ConfigureAwait(false);
        return AgentRuntimeFrameCodec.Encode(responseJson);
    }
}

public interface IAgentRuntimeStreamSession
{
    Task RunAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads length-prefixed JSON messages from a caller-owned stream and writes
/// responses back to the same stream. The session provides bounded concurrency,
/// serialized writes, and cancellation when the stream reaches EOF.
/// </summary>
public sealed class AgentRuntimeStreamSession : IAgentRuntimeStreamSession
{
    public const int MaximumConcurrentRequests = 16;
    public const int MaximumOutboundFrames = 256;
    private const int ReadBufferSize = 16 * 1024;

    private readonly IAgentRuntimeFrameEndpoint _endpoint;
    private readonly IAgentRuntimeHost? _eventSource;
    private long _droppedEventCount;

    /// <summary>
    /// Number of optional event frames discarded because the bounded outbound
    /// queue was full during the most recent session.
    /// </summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    public AgentRuntimeStreamSession(
        IAgentRuntimeFrameEndpoint endpoint,
        IAgentRuntimeHost? eventSource = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _eventSource = eventSource;
    }

    public async Task RunAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var concurrency = new SemaphoreSlim(MaximumConcurrentRequests, MaximumConcurrentRequests);
        var outbound = new OutboundQueue(MaximumOutboundFrames, this);
        var frameReader = new AgentRuntimeFrameReader();
        var dispatchTasks = new List<Task>();
        var buffer = GC.AllocateUninitializedArray<byte>(ReadBufferSize);
        var normalEof = false;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
        var eventSubscription = _eventSource?.Subscribe(
            @event => outbound.TryEnqueueEvent(@event));
        var writerTask = WriteOutboundAsync(stream, outbound.Reader, sessionCts);

        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(), sessionCts.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    if (frameReader.BufferedBytes > 0)
                        throw new EndOfStreamException("Runtime stream ended in the middle of a frame.");
                    normalEof = true;
                    await requestCts.CancelAsync().ConfigureAwait(false);
                    break;
                }

                frameReader.Append(buffer.AsSpan(0, bytesRead));
                while (frameReader.TryReadJson(out var requestJson))
                {
                    await concurrency.WaitAsync(sessionCts.Token).ConfigureAwait(false);
                    dispatchTasks.RemoveAll(static task => task.IsCompleted);
                    dispatchTasks.Add(DispatchFrameAsync(
                        requestJson!,
                        outbound,
                        concurrency,
                        requestCts.Token,
                        sessionCts));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            eventSubscription?.Dispose();
            if (!normalEof)
            {
                await requestCts.CancelAsync().ConfigureAwait(false);
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.WhenAll(dispatchTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            outbound.Complete();

            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task DispatchFrameAsync(
        string requestJson,
        OutboundQueue outbound,
        SemaphoreSlim concurrency,
        CancellationToken requestCancellationToken,
        CancellationTokenSource sessionCts)
    {
        try
        {
            var responseFrame = await _endpoint.DispatchJsonAsync(
                    requestJson,
                    requestCancellationToken)
                .ConfigureAwait(false);
            await outbound.WriteResponseAsync(responseFrame, sessionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            requestCancellationToken.IsCancellationRequested ||
            sessionCts.IsCancellationRequested)
        {
        }
        catch
        {
            sessionCts.Cancel();
        }
        finally
        {
            concurrency.Release();
        }
    }

    private static async Task WriteOutboundAsync(
        Stream stream,
        ChannelReader<OutboundFrame> outbound,
        CancellationTokenSource sessionCts)
    {
        try
        {
            await foreach (var item in outbound.ReadAllAsync(sessionCts.Token))
            {
                await stream.WriteAsync(item.Frame.AsMemory(), sessionCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(sessionCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            sessionCts.Cancel();
            throw;
        }
    }

    private sealed record OutboundFrame(byte[] Frame, bool IsEvent);

    private sealed class OutboundQueue
    {
        private readonly Channel<OutboundFrame> _channel;
        private readonly AgentRuntimeStreamSession _owner;
        private readonly SemaphoreSlim _responseGate = new(1, 1);
        private long _unreportedDroppedEvents;

        public OutboundQueue(int capacity, AgentRuntimeStreamSession owner)
        {
            _owner = owner;
            _channel = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public ChannelReader<OutboundFrame> Reader => _channel.Reader;

        public void TryEnqueueEvent(AgentRuntimeModuleEvent @event)
        {
            try
            {
                var eventJson = SerializeDefaultEvent(@event);
                if (!_channel.Writer.TryWrite(new(
                        AgentRuntimeFrameCodec.Encode(eventJson),
                        true)))
                {
                    Interlocked.Increment(ref _owner._droppedEventCount);
                    Interlocked.Increment(ref _unreportedDroppedEvents);
                }
            }
            catch
            {
                // A malformed optional event must not break the active request.
                Interlocked.Increment(ref _owner._droppedEventCount);
                Interlocked.Increment(ref _unreportedDroppedEvents);
            }
        }

        public async ValueTask WriteResponseAsync(byte[] frame, CancellationToken cancellationToken)
        {
            await _responseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var dropped = Interlocked.Exchange(ref _unreportedDroppedEvents, 0);
                if (dropped > 0)
                {
                    var overflow = new AgentRuntimeEventEnvelope(
                        "event",
                        "runtime/overflow",
                        "runtime",
                        string.Empty,
                        "runtime",
                        JsonSerializer.SerializeToElement(new { droppedEvents = dropped }, JsonOptions));
                    await _channel.Writer.WriteAsync(
                            new(AgentRuntimeFrameCodec.Encode(JsonSerializer.Serialize(overflow, JsonOptions)), true),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await _channel.Writer.WriteAsync(new(frame, false), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _responseGate.Release();
            }
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
            _responseGate.Dispose();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SerializeDefaultEvent(AgentRuntimeModuleEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var envelope = new AgentRuntimeEventEnvelope(
            "event",
            @event.EventName,
            @event.Module,
            @event.RequestId,
            @event.Method,
            @event.Payload == null
                ? null
                : JsonSerializer.SerializeToElement(@event.Payload, JsonOptions));
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }
}

/// <summary>
/// Length-prefixed UTF-8 framing for a future local IPC stream. The codec does
/// not open a socket or pipe; it only defines a transport-safe message boundary.
/// </summary>
public static class AgentRuntimeFrameCodec
{
    public const int HeaderLength = sizeof(int);
    public const int MaximumFrameBytes = AgentRuntimeContract.MaximumJsonRequestCharacters * 4;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload is required.", nameof(json));

        var payload = StrictUtf8.GetBytes(json);
        ValidateLength(payload.Length);
        var frame = GC.AllocateUninitializedArray<byte>(HeaderLength + payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, HeaderLength), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    internal static string Decode(ReadOnlySpan<byte> payload)
    {
        ValidateLength(payload.Length);
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Runtime frame payload is not valid UTF-8.", exception);
        }
    }

    internal static string DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength)
            throw new InvalidDataException("Runtime frame header ended early.");

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(frame[..HeaderLength]);
        ValidateLength(payloadLength);
        var expectedLength = checked(HeaderLength + payloadLength);
        if (frame.Length != expectedLength)
            throw new InvalidDataException("Runtime frame has an invalid payload length.");

        return Decode(frame.Slice(HeaderLength, payloadLength));
    }

    internal static void ValidateLength(int length)
    {
        if (length <= 0 || length > MaximumFrameBytes)
            throw new InvalidDataException($"Invalid runtime frame length: {length}.");
    }
}

/// <summary>
/// Incremental reader for length-prefixed runtime messages. It handles partial
/// reads and multiple frames in one read while enforcing the frame size bound.
/// </summary>
public sealed class AgentRuntimeFrameReader
{
    private byte[] _buffer = new byte[AgentRuntimeFrameCodec.HeaderLength];
    private int _count;

    public int BufferedBytes => _count;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        EnsureCapacity(_count + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;

        if (_count >= AgentRuntimeFrameCodec.HeaderLength)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(
                _buffer.AsSpan(0, AgentRuntimeFrameCodec.HeaderLength));
            AgentRuntimeFrameCodec.ValidateLength(length);
        }
    }

    public bool TryReadJson(out string? json)
    {
        json = null;
        if (_count < AgentRuntimeFrameCodec.HeaderLength)
            return false;

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(
            _buffer.AsSpan(0, AgentRuntimeFrameCodec.HeaderLength));
        AgentRuntimeFrameCodec.ValidateLength(payloadLength);
        var frameLength = checked(AgentRuntimeFrameCodec.HeaderLength + payloadLength);
        if (_count < frameLength)
            return false;

        json = AgentRuntimeFrameCodec.Decode(
            _buffer.AsSpan(AgentRuntimeFrameCodec.HeaderLength, payloadLength));
        var remaining = _count - frameLength;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_buffer, frameLength, _buffer, 0, remaining);
        }

        _count = remaining;
        return true;
    }

    private void EnsureCapacity(int requiredCapacity)
    {
        if (requiredCapacity > AgentRuntimeFrameCodec.HeaderLength + AgentRuntimeFrameCodec.MaximumFrameBytes)
            throw new InvalidDataException("Runtime frame buffer exceeded the maximum frame size.");
        if (requiredCapacity <= _buffer.Length)
            return;

        var capacity = Math.Min(
            AgentRuntimeFrameCodec.HeaderLength + AgentRuntimeFrameCodec.MaximumFrameBytes,
            Math.Max(requiredCapacity, _buffer.Length * 2));
        Array.Resize(ref _buffer, capacity);
    }
}
