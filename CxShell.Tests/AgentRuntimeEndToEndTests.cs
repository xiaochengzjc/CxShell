using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeEndToEndTests
{
    [Fact]
    public async Task ClientTransportReachesStreamSessionAndHostEndToEnd()
    {
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => []));
        using var adapter = new AgentRuntimeSessionAdapter(gateway);
        using var host = new AgentRuntimeHost([adapter, new TestModule()]);
        using var pair = DuplexStream.CreatePair();
        using var serverCancellation = new CancellationTokenSource();
        var server = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host),
            host);
        var serverTask = server.RunAsync(pair.Server, serverCancellation.Token);

        await using var transport = new AgentRuntimeStreamTransport(pair.Client, leaveOpen: false);
        var client = new AgentRuntimeClient(transport);
        var events = new List<AgentRuntimeEventEnvelope>();
        using var subscription = client.SubscribeEvents(@event =>
        {
            lock (events)
                events.Add(@event);
        });

        var initialized = await client.SendResultAsync<AgentRuntimeInitializeResult>(
            AgentRuntimeMethodNames.Initialize);
        var runtimeInfo = await client.SendResultAsync<AgentRuntimeInfoResult>(
            AgentRuntimeMethodNames.RuntimeInfo);
        var concurrent = await Task.WhenAll(
            Enumerable.Range(1, 12).Select(index => client.SendResultAsync<EchoResult>(
                "agent/echo",
                new { value = index, delayMs = 12 - index },
                $"e2e-{index}")));

        Assert.True(initialized.Ok);
        Assert.Equal(AgentRuntimeContract.Protocol, runtimeInfo.Protocol);
        Assert.Equal(12, concurrent.Length);
        Assert.Equal(
            Enumerable.Range(1, 12),
            concurrent.Select(result => result.Value).OrderBy(value => value));
        lock (events)
        {
            Assert.Contains(events, @event =>
                @event.EventName == "progress" && @event.RequestId == "e2e-1");
        }

        await serverCancellation.CancelAsync();
        await serverTask;
    }

    [Fact]
    public async Task ClientCancellationAndPeerCloseCompleteThePendingRequest()
    {
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => []));
        using var adapter = new AgentRuntimeSessionAdapter(gateway);
        using var host = new AgentRuntimeHost([adapter, new TestModule()]);
        using var pair = DuplexStream.CreatePair();
        using var serverCancellation = new CancellationTokenSource();
        var server = new AgentRuntimeStreamSession(new AgentRuntimeFrameEndpoint(host));
        var serverTask = server.RunAsync(pair.Server, serverCancellation.Token);
        await using var transport = new AgentRuntimeStreamTransport(pair.Client, leaveOpen: false);

        using var requestCancellation = new CancellationTokenSource();
        var pending = transport.SendAsync(
            "{\"requestId\":\"e2e-block\",\"method\":\"agent/block\"}",
            requestCancellation.Token);
        await pair.WaitForWriteAsync();
        await requestCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await serverCancellation.CancelAsync();
        pair.CompleteInput();
        await serverTask;
    }

    private sealed record EchoResult(
        [property: JsonPropertyName("value")] int Value);

    private sealed class TestModule : IAgentRuntimeModule
    {
        public string Name => "e2e";
        public IReadOnlyCollection<string> Methods => ["agent/echo", "agent/block"];

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            if (request.Method == "agent/block")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            var value = request.Parameters.ValueKind == JsonValueKind.Object &&
                        request.Parameters.TryGetProperty("value", out var valueElement) &&
                        valueElement.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            var delay = request.Parameters.ValueKind == JsonValueKind.Object &&
                        request.Parameters.TryGetProperty("delayMs", out var delayElement) &&
                        delayElement.TryGetInt32(out var requestedDelay)
                ? Math.Max(0, requestedDelay)
                : 0;
            await context.EmitEventAsync("progress", new { value });
            if (delay > 0)
                await Task.Delay(delay, context.CancellationToken);

            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true,
                Result = JsonSerializer.SerializeToElement(new EchoResult(value))
            };
        }
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Channel<byte[]> _incoming;
        private readonly Channel<byte[]> _outgoing;
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<bool>> _writeWaiters = [];
        private byte[]? _current;
        private int _offset;
        private int _writeCount;
        private bool _disposed;

        private DuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
        {
            _incoming = incoming;
            _outgoing = outgoing;
        }

        public static Pair CreatePair()
        {
            var leftToRight = Channel.CreateUnbounded<byte[]>();
            var rightToLeft = Channel.CreateUnbounded<byte[]>();
            var client = new DuplexStream(rightToLeft, leftToRight);
            var server = new DuplexStream(leftToRight, rightToLeft);
            return new Pair(client, server);
        }

        public Task WaitForWriteAsync()
        {
            lock (_gate)
            {
                if (_writeCount > 0)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _writeWaiters.Add(waiter);
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public void CompleteInput() => _incoming.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current == null || _offset >= _current.Length)
            {
                try
                {
                    _current = await _incoming.Reader.ReadAsync(cancellationToken);
                    _offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_outgoing.Writer.TryWrite(buffer.ToArray()))
                throw new IOException("The loopback stream is closed.");

            TaskCompletionSource<bool>[] waiters;
            lock (_gate)
            {
                _writeCount++;
                waiters = _writeWaiters.ToArray();
                _writeWaiters.Clear();
            }

            foreach (var waiter in waiters)
                waiter.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _incoming.Writer.TryComplete();
                _outgoing.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            return ValueTask.CompletedTask;
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

        public sealed record Pair(DuplexStream Client, DuplexStream Server) : IDisposable
        {
            public void CompleteInput() => Client._incoming.Writer.TryComplete();
            public Task WaitForWriteAsync() => Client.WaitForWriteAsync();
            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
            }
        }
    }
}
