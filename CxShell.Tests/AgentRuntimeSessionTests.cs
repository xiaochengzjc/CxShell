using System.Text.Json;
using System.Collections.Concurrent;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeSessionTests
{
    [Fact]
    public async Task SessionNegotiatesOnceAndCachesTheAdvertisedContract()
    {
        var transport = new DiscoveryTransport();
        using var session = new AgentRuntimeSession(new AgentRuntimeClient(transport));

        var info = await session.GetRuntimeInfoAsync("info-1");
        var capability = await session.CheckCapabilityAsync("agent.session.list", "capability-1");

        Assert.Equal(AgentRuntimeSessionState.Ready, session.State);
        Assert.Equal(1, transport.InitializeCount);
        Assert.Equal(AgentRuntimeContract.Protocol, info.Protocol);
        Assert.True(capability.Supported);
    }

    [Fact]
    public async Task SessionRejectsMethodsThatWereNotAdvertised()
    {
        var transport = new DiscoveryTransport();
        using var session = new AgentRuntimeSession(new AgentRuntimeClient(transport));

        var exception = await Assert.ThrowsAsync<AgentRuntimeMethodNotSupportedException>(
            () => session.SendAsync("agent/not-advertised"));

        Assert.Equal("agent/not-advertised", exception.Method);
        Assert.Equal(1, transport.InitializeCount);
        Assert.Equal(0, transport.RequestCountFor("agent/not-advertised"));
    }

    [Fact]
    public async Task SessionCanRetryAfterHandshakeFailure()
    {
        var transport = new DiscoveryTransport { FailFirstInitialization = true };
        using var session = new AgentRuntimeSession(new AgentRuntimeClient(transport));

        await Assert.ThrowsAsync<AgentRuntimeRequestException>(
            () => session.InitializeAsync());
        Assert.Equal(AgentRuntimeSessionState.Failed, session.State);

        var result = await session.InitializeAsync();

        Assert.Equal(AgentRuntimeSessionState.Ready, session.State);
        Assert.Equal(AgentRuntimeContract.Protocol, result.Protocol);
        Assert.Equal(2, transport.InitializeCount);
    }

    [Fact]
    public async Task StatusSnapshotTracksHandshakeLifecycleAndRequestId()
    {
        var transport = new DiscoveryTransport { InitializationDelay = TimeSpan.FromMilliseconds(20) };
        using var session = new AgentRuntimeSession(new AgentRuntimeClient(transport));
        var statuses = new List<AgentRuntimeSessionStatus>();
        session.StatusChanged += statuses.Add;

        await session.InitializeAsync(requestId: "init-status-1");

        Assert.Collection(
            statuses,
            initializing =>
            {
                Assert.Equal(AgentRuntimeSessionState.Initializing, initializing.State);
                Assert.Equal(1, initializing.InitializationAttempt);
                Assert.Equal("init-status-1", initializing.RequestId);
                Assert.Null(initializing.Error);
            },
            ready =>
            {
                Assert.Equal(AgentRuntimeSessionState.Ready, ready.State);
                Assert.Equal(1, ready.InitializationAttempt);
                Assert.Equal("init-status-1", ready.RequestId);
                Assert.Null(ready.Error);
            });
        Assert.Equal(session.Status, statuses[^1]);
    }

    [Fact]
    public async Task FailedStatusContainsStableRuntimeErrorCodeAndRetryAttempt()
    {
        var transport = new DiscoveryTransport { FailFirstInitialization = true };
        using var session = new AgentRuntimeSession(new AgentRuntimeClient(transport));
        var statuses = new List<AgentRuntimeSessionStatus>();
        session.StatusChanged += statuses.Add;

        await Assert.ThrowsAsync<AgentRuntimeRequestException>(() => session.InitializeAsync());

        var failed = Assert.Single(statuses, status => status.State == AgentRuntimeSessionState.Failed);
        Assert.Equal(1, failed.InitializationAttempt);
        Assert.Equal(AgentRuntimeErrorCodes.Internal, failed.ErrorCode);
        Assert.Equal("temporary", failed.Error);

        await session.InitializeAsync();

        Assert.Equal(AgentRuntimeSessionState.Ready, session.Status.State);
        Assert.Equal(2, session.Status.InitializationAttempt);
        Assert.Equal(AgentRuntimeSessionState.Initializing, statuses[^2].State);
        Assert.Equal(AgentRuntimeSessionState.Ready, statuses[^1].State);
    }

    [Fact]
    public async Task ConcurrentFirstCallsShareOneHandshake()
    {
        var transport = new DiscoveryTransport { InitializationDelay = TimeSpan.FromMilliseconds(30) };
        using var session = new AgentRuntimeSession(new AgentRuntimeClient(transport));

        await Task.WhenAll(
            session.GetRuntimeInfoAsync("info-concurrent"),
            session.CheckCapabilityAsync("agent.session.list", "capability-concurrent"));

        Assert.Equal(1, transport.InitializeCount);
        Assert.Equal(AgentRuntimeSessionState.Ready, session.State);
    }

    private sealed class DiscoveryTransport : IAgentRuntimeTransport
    {
        private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);
        private int _initializeCount;

        public bool FailFirstInitialization { get; init; }
        public TimeSpan InitializationDelay { get; init; }
        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public int RequestCountFor(string method)
            => _requests.TryGetValue(method, out var count) ? count : 0;

        public async Task<string> SendAsync(
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            var requestId = root.GetProperty("requestId").GetString()!;
            var method = root.GetProperty("method").GetString()!;
            _requests.AddOrUpdate(method, 1, static (_, count) => count + 1);

            if (method == AgentRuntimeMethodNames.Initialize)
            {
                var count = Interlocked.Increment(ref _initializeCount);
                if (InitializationDelay > TimeSpan.Zero)
                    await Task.Delay(InitializationDelay, cancellationToken);
                if (FailFirstInitialization && count == 1)
                {
                    return $"{{\"requestId\":\"{requestId}\",\"ok\":false,\"errorCode\":\"internal_error\",\"error\":\"temporary\"}}";
                }

                return $"{{\"requestId\":\"{requestId}\",\"ok\":true,\"result\":{{\"ok\":true,\"runtime\":\"cxshell-session-gateway\",\"version\":\"0.1\",\"protocol\":\"cxshell-agent\",\"protocolVersion\":\"1\",\"methods\":[\"initialize\",\"agent/runtime-info\",\"capabilities/check\",\"runtime/cancel\"],\"capabilities\":[\"agent.session.list\"]}}}}";
            }

            if (method == AgentRuntimeMethodNames.RuntimeInfo)
            {
                return $"{{\"requestId\":\"{requestId}\",\"ok\":true,\"result\":{{\"runtime\":\"cxshell-session-gateway\",\"protocol\":\"cxshell-agent\",\"protocolVersion\":\"1\",\"runtimeVersion\":\"0.1\",\"methods\":[\"initialize\"],\"capabilities\":[],\"supportedProtocols\":[]}}}}";
            }

            if (method == AgentRuntimeMethodNames.CapabilitiesCheck)
            {
                return $"{{\"requestId\":\"{requestId}\",\"ok\":true,\"result\":{{\"supported\":true,\"capability\":\"agent.session.list\"}}}}";
            }

            return $"{{\"requestId\":\"{requestId}\",\"ok\":true}}";
        }
    }
}
