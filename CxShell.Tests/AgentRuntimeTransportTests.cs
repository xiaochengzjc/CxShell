using System.Text;
using System.Text.Json;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeTransportTests
{
    [Fact]
    public async Task InProcessTransportDelegatesToJsonEndpoint()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        IAgentRuntimeTransport transport = new InProcessAgentRuntimeTransport(host);

        var responseJson = await transport.SendAsync(
            "{\"requestId\":\"transport-1\",\"method\":\"agent/echo\"}");

        Assert.Contains("\"requestId\":\"transport-1\"", responseJson, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameEndpointDispatchesOneFrameAndReturnsOneFrame()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        IAgentRuntimeFrameEndpoint endpoint = new AgentRuntimeFrameEndpoint(host);
        var requestFrame = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"frame-1\",\"method\":\"agent/echo\"}");

        var responseFrame = await endpoint.DispatchAsync(requestFrame);
        var responseJson = ReadSingleFrame(responseFrame);

        Assert.Contains("\"requestId\":\"frame-1\"", responseJson, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamSessionHandlesPartialReadsAndMultipleFrames()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        IAgentRuntimeStreamSession session = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host));
        var input = AgentRuntimeFrameCodec.Encode(
                "{\"requestId\":\"stream-1\",\"method\":\"agent/echo\"}")
            .Concat(AgentRuntimeFrameCodec.Encode(
                "{\"requestId\":\"stream-2\",\"method\":\"agent/echo\"}"))
            .ToArray();
        using var stream = new TestDuplexStream(input, readChunkSize: 3, holdAfterInput: true)
        {
            ExpectedWrites = 2
        };
        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(stream, cancellation.Token);

        await stream.WritesReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await run;

        var reader = new AgentRuntimeFrameReader();
        reader.Append(stream.GetWrittenBytes());
        var responses = new List<string>();
        while (reader.TryReadJson(out var json))
            responses.Add(json!);

        Assert.Equal(2, responses.Count);
        Assert.Contains("stream-1", responses[0] + responses[1], StringComparison.Ordinal);
        Assert.Contains("stream-2", responses[0] + responses[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamSessionForwardsModuleEventsBeforeTheResponse()
    {
        var module = new EventModule();
        using var host = new AgentRuntimeHost([module]);
        IAgentRuntimeStreamSession session = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host),
            host);
        var request = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-event\",\"method\":\"agent/event\"}");
        using var stream = new TestDuplexStream(request, request.Length, holdAfterInput: true)
        {
            ExpectedWrites = 2
        };
        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(stream, cancellation.Token);

        await stream.WritesReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await run;

        var reader = new AgentRuntimeFrameReader();
        reader.Append(stream.GetWrittenBytes());
        var responses = new List<string>();
        while (reader.TryReadJson(out var json))
            responses.Add(json!);

        Assert.Equal(2, responses.Count);
        Assert.Contains("\"type\":\"event\"", responses[0], StringComparison.Ordinal);
        Assert.Contains("\"event\":\"progress\"", responses[0], StringComparison.Ordinal);
        Assert.Contains("stream-event", responses[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamSessionFlushesCompletedResponsesBeforeNormalEof()
    {
        using var host = new AgentRuntimeHost([new DelayedEchoModule()]);
        IAgentRuntimeStreamSession session = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host));
        var request = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-eof-response\",\"method\":\"agent/delayed-echo\"}");
        using var stream = new TestDuplexStream(
            request,
            readChunkSize: request.Length,
            holdAfterInput: false);

        await session.RunAsync(stream);

        var reader = new AgentRuntimeFrameReader();
        reader.Append(stream.GetWrittenBytes());
        Assert.True(reader.TryReadJson(out var response));
        Assert.Contains("stream-eof-response", response, StringComparison.Ordinal);
        Assert.False(reader.TryReadJson(out _));
    }

    [Fact]
    public async Task StreamSessionCancelsActiveRequestWhenStreamEnds()
    {
        var module = new BlockingModule();
        using var host = new AgentRuntimeHost([module]);
        IAgentRuntimeStreamSession session = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host));
        var request = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-eof\",\"method\":\"agent/block\"}");
        using var stream = new TestDuplexStream(request, readChunkSize: request.Length, holdAfterInput: false);

        await session.RunAsync(stream);

        await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await module.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StreamSessionDeliversOverflowNotificationWhileRequestHasNoResponseYet()
    {
        var module = new BurstBlockingModule();
        using var host = new AgentRuntimeHost([module]);
        IAgentRuntimeStreamSession session = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host),
            host);
        var request = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-overflow\",\"method\":\"agent/burst\"}");
        using var stream = new GatedWriteDuplexStream(request);
        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(stream, cancellation.Token);

        await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, stream.WrittenFrameCount);

        stream.ReleaseWrites();
        await stream.OverflowWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(((AgentRuntimeStreamSession)session).DroppedEventCount > 0);

        await cancellation.CancelAsync();
        await run;

        Assert.Contains(
            stream.GetWrittenJson(),
            json => json.Contains("runtime/overflow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamSessionRejectsTruncatedFrames()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        IAgentRuntimeStreamSession session = new AgentRuntimeStreamSession(
            new AgentRuntimeFrameEndpoint(host));
        var completeFrame = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"stream-truncated\",\"method\":\"agent/echo\"}");
        using var stream = new TestDuplexStream(
            completeFrame.AsMemory(0, completeFrame.Length - 1).ToArray(),
            readChunkSize: completeFrame.Length,
            holdAfterInput: false);

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.RunAsync(stream));
    }

    [Fact]
    public void FrameReaderHandlesPartialAndMultipleFrames()
    {
        var first = AgentRuntimeFrameCodec.Encode("{\"requestId\":\"one\"}");
        var second = AgentRuntimeFrameCodec.Encode("{\"requestId\":\"二\"}");
        var combined = first.Concat(second).ToArray();
        var reader = new AgentRuntimeFrameReader();

        reader.Append(combined.AsSpan(0, 2));
        Assert.False(reader.TryReadJson(out _));
        reader.Append(combined.AsSpan(2, 7));
        Assert.False(reader.TryReadJson(out _));
        reader.Append(combined.AsSpan(9));

        Assert.True(reader.TryReadJson(out var firstJson));
        Assert.Equal("{\"requestId\":\"one\"}", firstJson);
        Assert.True(reader.TryReadJson(out var secondJson));
        Assert.Equal("{\"requestId\":\"二\"}", secondJson);
        Assert.False(reader.TryReadJson(out _));
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact]
    public void FrameReaderRejectsInvalidLengthAndInvalidUtf8()
    {
        var reader = new AgentRuntimeFrameReader();
        var invalidLength = new byte[AgentRuntimeFrameCodec.HeaderLength];
        invalidLength[0] = 0x7f;
        Assert.Throws<InvalidDataException>(() => reader.Append(invalidLength));

        var invalidUtf8 = new byte[AgentRuntimeFrameCodec.HeaderLength + 1];
        invalidUtf8[3] = 1;
        invalidUtf8[4] = 0xff;
        reader = new AgentRuntimeFrameReader();
        reader.Append(invalidUtf8);
        Assert.Throws<InvalidDataException>(() => reader.TryReadJson(out _));
    }

    [Fact]
    public void FrameCodecRejectsOversizedPayloads()
    {
        var oversized = new string('x', AgentRuntimeFrameCodec.MaximumFrameBytes + 1);
        Assert.Throws<InvalidDataException>(() => AgentRuntimeFrameCodec.Encode(oversized));
        Assert.Equal(
            AgentRuntimeContract.MaximumJsonRequestCharacters * 4,
            AgentRuntimeFrameCodec.MaximumFrameBytes);
    }

    [Fact]
    public async Task FrameEndpointRejectsTruncatedFrames()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        var endpoint = new AgentRuntimeFrameEndpoint(host);
        var frame = AgentRuntimeFrameCodec.Encode(
            "{\"requestId\":\"frame-2\",\"method\":\"agent/echo\"}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => endpoint.DispatchAsync(frame.AsMemory(0, frame.Length - 1)));
    }

    private static string ReadSingleFrame(byte[] frame)
    {
        var reader = new AgentRuntimeFrameReader();
        reader.Append(frame);
        Assert.True(reader.TryReadJson(out var json));
        Assert.False(reader.TryReadJson(out _));
        return json!;
    }

    private sealed class EchoModule : IAgentRuntimeModule
    {
        public string Name => "echo";
        public IReadOnlyCollection<string> Methods => ["agent/echo"];

        public Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
            => Task.FromResult(new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            });
    }

    private sealed class EventModule : IAgentRuntimeModule
    {
        public string Name => "events";
        public IReadOnlyCollection<string> Methods => ["agent/event"];

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            await context.EmitEventAsync("progress", new { percent = 50 });
            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }
    }

    private sealed class DelayedEchoModule : IAgentRuntimeModule
    {
        public string Name => "delayed-echo";
        public IReadOnlyCollection<string> Methods => ["agent/delayed-echo"];

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            await Task.Delay(50);
            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }
    }

    private sealed class BlockingModule : IAgentRuntimeModule
    {
        public string Name => "blocking";
        public IReadOnlyCollection<string> Methods => ["agent/block"];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }
    }

    private sealed class BurstBlockingModule : IAgentRuntimeModule
    {
        public string Name => "burst";
        public IReadOnlyCollection<string> Methods => ["agent/burst"];
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            for (var index = 0; index < AgentRuntimeStreamSession.MaximumOutboundFrames + 32; index++)
            {
                await context.EmitEventIgnoringCancellationAsync(
                    "progress",
                    new { index });
            }

            Started.TrySetResult();
            await Release.Task.WaitAsync(context.CancellationToken);
            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }
    }

    private sealed class TestDuplexStream : Stream
    {
        private readonly byte[] _input;
        private readonly int _readChunkSize;
        private readonly bool _holdAfterInput;
        private readonly object _writeGate = new();
        private readonly MemoryStream _output = new();
        private int _inputOffset;

        public TestDuplexStream(byte[] input, int readChunkSize, bool holdAfterInput)
        {
            _input = input;
            _readChunkSize = readChunkSize;
            _holdAfterInput = holdAfterInput;
        }

        public int ExpectedWrites { get; init; }
        public TaskCompletionSource WritesReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] GetWrittenBytes()
        {
            lock (_writeGate)
                return _output.ToArray();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_inputOffset < _input.Length)
            {
                var count = Math.Min(
                    Math.Min(_readChunkSize, buffer.Length),
                    _input.Length - _inputOffset);
                _input.AsMemory(_inputOffset, count).CopyTo(buffer);
                _inputOffset += count;
                return count;
            }

            if (_holdAfterInput)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_writeGate)
            {
                _output.Write(buffer.Span);
                if (ExpectedWrites > 0 && CountFrames(_output.ToArray()) >= ExpectedWrites)
                    WritesReady.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _input.Length;
        public override long Position { get => _inputOffset; set => throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static int CountFrames(byte[] bytes)
        {
            var reader = new AgentRuntimeFrameReader();
            reader.Append(bytes);
            var count = 0;
            while (reader.TryReadJson(out _))
                count++;
            return count;
        }
    }

    private sealed class GatedWriteDuplexStream : Stream
    {
        private readonly byte[] _input;
        private readonly object _gate = new();
        private readonly MemoryStream _output = new();
        private readonly TaskCompletionSource _writeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inputOffset;
        private int _firstWrite = 1;

        public GatedWriteDuplexStream(byte[] input)
        {
            _input = input;
        }

        public TaskCompletionSource OverflowWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WrittenFrameCount
        {
            get
            {
                lock (_gate)
                    return CountFrames(_output.ToArray());
            }
        }

        public void ReleaseWrites() => _writeRelease.TrySetResult();

        public string[] GetWrittenJson()
        {
            lock (_gate)
            {
                var reader = new AgentRuntimeFrameReader();
                reader.Append(_output.ToArray());
                var json = new List<string>();
                while (reader.TryReadJson(out var value))
                    json.Add(value!);
                return json.ToArray();
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_inputOffset < _input.Length)
            {
                var count = Math.Min(buffer.Length, _input.Length - _inputOffset);
                _input.AsMemory(_inputOffset, count).CopyTo(buffer);
                _inputOffset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _firstWrite, 0) == 1)
                await _writeRelease.Task.WaitAsync(cancellationToken);

            var frame = buffer.ToArray();
            lock (_gate)
                _output.Write(frame);

            var reader = new AgentRuntimeFrameReader();
            reader.Append(frame);
            if (reader.TryReadJson(out var json) &&
                json!.Contains("runtime/overflow", StringComparison.Ordinal))
            {
                OverflowWritten.TrySetResult();
            }
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _input.Length;
        public override long Position { get => _inputOffset; set => throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static int CountFrames(byte[] bytes)
        {
            var reader = new AgentRuntimeFrameReader();
            reader.Append(bytes);
            var count = 0;
            while (reader.TryReadJson(out _))
                count++;
            return count;
        }
    }
}
