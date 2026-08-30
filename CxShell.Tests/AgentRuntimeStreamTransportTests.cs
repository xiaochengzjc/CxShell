using System.Text.Json;
using System.Threading.Channels;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeStreamTransportTests
{
    [Fact]
    public async Task StreamTransportCorrelatesResponsesAndForwardsEvents()
    {
        using var stream = new ChannelDuplexStream();
        await using var transport = new AgentRuntimeStreamTransport(stream);
        var client = new AgentRuntimeClient(transport);
        var received = new TaskCompletionSource<AgentRuntimeEventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.SubscribeEvents(@event =>
        {
            received.TrySetResult(@event);
        });

        var responseTask = client.SendResultAsync<EchoResult>(
            "agent/echo",
            new { value = 7 },
            "stream-client-1");
        await stream.WaitForWriteCountAsync(1);

        var requestReader = new AgentRuntimeFrameReader();
        requestReader.Append(stream.GetWriteBytes(0));
        Assert.True(requestReader.TryReadJson(out var requestJson));
        using var request = JsonDocument.Parse(requestJson!);
        Assert.Equal("stream-client-1", request.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(7, request.RootElement.GetProperty("params").GetProperty("value").GetInt32());

        var eventFrame = AgentRuntimeFrameCodec.Encode(
            "{\"type\":\"event\",\"event\":\"progress\",\"module\":\"session-gateway\",\"requestId\":\"run-1\",\"method\":\"agent/run\",\"payload\":{\"percent\":50}}");
        var responseFrame = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-client-1\",\"ok\":true,\"result\":{\"value\":7}}");
        var combined = eventFrame.Concat(responseFrame).ToArray();
        stream.Enqueue(combined[..3]);
        stream.Enqueue(combined[3..]);

        var result = await responseTask;
        var @event = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(7, result.Value);
        Assert.Equal("progress", @event.EventName);
        Assert.Equal("run-1", @event.RequestId);
    }

    [Fact]
    public async Task StreamTransportMatchesConcurrentResponsesByRequestId()
    {
        using var stream = new ChannelDuplexStream();
        await using var transport = new AgentRuntimeStreamTransport(stream);
        var first = transport.SendAsync(
            "{\"requestId\":\"stream-one\",\"method\":\"ping\"}");
        var second = transport.SendAsync(
            "{\"requestId\":\"stream-two\",\"method\":\"ping\"}");
        await stream.WaitForWriteCountAsync(2);

        stream.Enqueue(AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-two\",\"ok\":true}"));
        stream.Enqueue(AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-one\",\"ok\":true}"));

        Assert.Contains("stream-one", await first, StringComparison.Ordinal);
        Assert.Contains("stream-two", await second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamTransportFailsPendingRequestsWhenPeerCloses()
    {
        using var stream = new ChannelDuplexStream();
        await using var transport = new AgentRuntimeStreamTransport(stream);
        var pending = transport.SendAsync(
            "{\"requestId\":\"stream-close\",\"method\":\"ping\"}");
        await stream.WaitForWriteCountAsync(1);
        stream.CompleteInput();

        await Assert.ThrowsAsync<EndOfStreamException>(() => pending);
    }

    [Fact]
    public async Task StreamTransportRejectsDuplicateActiveRequestIds()
    {
        using var stream = new ChannelDuplexStream();
        await using var transport = new AgentRuntimeStreamTransport(stream);
        var first = transport.SendAsync(
            "{\"requestId\":\"stream-duplicate\",\"method\":\"ping\"}");
        await stream.WaitForWriteCountAsync(1);

        await Assert.ThrowsAsync<AgentRuntimeProtocolException>(() => transport.SendAsync(
            "{\"requestId\":\"stream-duplicate\",\"method\":\"ping\"}"));

        stream.Enqueue(AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-duplicate\",\"ok\":true}"));
        Assert.Contains("stream-duplicate", await first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamTransportSendsProtocolCancellationRequest()
    {
        using var stream = new ChannelDuplexStream();
        await using var transport = new AgentRuntimeStreamTransport(stream);
        var cancellation = transport.RequestCancellationAsync("target-request-1");

        await stream.WaitForWriteCountAsync(1);
        var requestReader = new AgentRuntimeFrameReader();
        requestReader.Append(stream.GetWriteBytes(0));
        Assert.True(requestReader.TryReadJson(out var requestJson));
        using var request = JsonDocument.Parse(requestJson!);

        Assert.Equal(AgentRuntimeMethodNames.RequestCancel, request.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "target-request-1",
            request.RootElement.GetProperty("params").GetProperty("requestId").GetString());

        var requestId = request.RootElement.GetProperty("requestId").GetString();
        stream.Enqueue(AgentRuntimeFrameCodec.Encode(
            $"{{\"requestId\":\"{requestId}\",\"ok\":true,\"result\":{{\"cancelled\":true,\"requestId\":\"target-request-1\"}}}}"));

        await cancellation;
    }

    private sealed record EchoResult(int Value);

    private sealed class ChannelDuplexStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly object _writeGate = new();
        private readonly List<byte[]> _writes = [];
        private readonly List<TaskCompletionSource<bool>> _writeWaiters = [];
        private byte[]? _currentChunk;
        private int _currentOffset;
        private bool _disposed;

        public void Enqueue(byte[] bytes)
        {
            if (bytes.Length == 0)
                return;
            if (!_incoming.Writer.TryWrite(bytes))
                throw new InvalidOperationException("The test stream is closed.");
        }

        public void CompleteInput() => _incoming.Writer.TryComplete();

        public async Task WaitForWriteCountAsync(int expectedCount)
        {
            while (true)
            {
                Task waitTask;
                lock (_writeGate)
                {
                    if (_writes.Count >= expectedCount)
                        return;

                    var waiter = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _writeWaiters.Add(waiter);
                    waitTask = waiter.Task;
                }

                await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public byte[] GetWriteBytes(int index)
        {
            lock (_writeGate)
                return _writes[index].ToArray();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                try
                {
                    _currentChunk = await _incoming.Reader.ReadAsync(cancellationToken);
                    _currentOffset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _currentChunk.Length - _currentOffset);
            _currentChunk.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>[] waiters;
            lock (_writeGate)
            {
                _writes.Add(buffer.ToArray());
                waiters = _writeWaiters.ToArray();
                _writeWaiters.Clear();
            }

            foreach (var waiter in waiters)
                waiter.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _incoming.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
