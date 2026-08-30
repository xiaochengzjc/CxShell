using System.Text.Json;
using System.Text.Json.Serialization;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeClientTests
{
    [Fact]
    public async Task ClientUsesParamsAndDeserializesTypedResults()
    {
        var transport = new RecordingTransport(
            "{\"requestId\":\"client-1\",\"ok\":true,\"result\":{\"value\":42}}");
        var client = new AgentRuntimeClient(transport);

        var result = await client.SendResultAsync<EchoResult>(
            " agent/echo ",
            new { value = 42 },
            " client-1 ");

        Assert.Equal(42, result.Value);
        using var request = JsonDocument.Parse(transport.LastRequest!);
        Assert.Equal("client-1", request.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("agent/echo", request.RootElement.GetProperty("method").GetString());
        Assert.Equal(42, request.RootElement.GetProperty("params").GetProperty("value").GetInt32());
        Assert.False(request.RootElement.TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task ClientProvidesTypedRuntimeDiscoveryHelpers()
    {
        var transport = new RecordingTransport(request =>
        {
            using var document = JsonDocument.Parse(request);
            var method = document.RootElement.GetProperty("method").GetString();
            return method switch
            {
                AgentRuntimeMethodNames.Initialize =>
                    "{\"requestId\":\"initialize-1\",\"ok\":true,\"result\":{\"ok\":true,\"runtime\":\"cxshell-session-gateway\",\"version\":\"0.1\",\"protocol\":\"cxshell-agent\",\"protocolVersion\":\"1\",\"methods\":[],\"capabilities\":[]}}",
                AgentRuntimeMethodNames.RuntimeInfo =>
                    "{\"requestId\":\"runtime-info-1\",\"ok\":true,\"result\":{\"runtime\":\"cxshell-session-gateway\",\"protocol\":\"cxshell-agent\",\"protocolVersion\":\"1\",\"runtimeVersion\":\"0.1\",\"methods\":[],\"capabilities\":[],\"supportedProtocols\":[]}}",
                AgentRuntimeMethodNames.CapabilitiesCheck =>
                    "{\"requestId\":\"capability-1\",\"ok\":true,\"result\":{\"supported\":true,\"capability\":\"agent.session.list\"}}",
                _ => throw new InvalidOperationException($"Unexpected method: {method}")
            };
        });
        var client = new AgentRuntimeClient(transport);

        var initialized = await client.InitializeAsync(
            AgentRuntimeContract.Protocol,
            AgentRuntimeContract.ProtocolVersion,
            "initialize-1");
        var runtimeInfo = await client.GetRuntimeInfoAsync("runtime-info-1");
        var capability = await client.CheckCapabilityAsync("agent.session.list", "capability-1");

        Assert.Equal(AgentRuntimeContract.Protocol, initialized.Protocol);
        Assert.Equal(AgentRuntimeContract.ProtocolVersion, initialized.ProtocolVersion);
        Assert.Equal(AgentRuntimeContract.Protocol, runtimeInfo.Protocol);
        Assert.True(capability.Supported);
        Assert.Equal("agent.session.list", capability.Capability);
    }

    [Fact]
    public async Task ClientDiscoveryHelpersKeepInitializeWithoutParametersCompatible()
    {
        var transport = new RecordingTransport(
            "{\"requestId\":\"initialize-legacy\",\"ok\":true,\"result\":{\"ok\":true,\"runtime\":\"cxshell-session-gateway\",\"version\":\"0.1\"}}");
        var client = new AgentRuntimeClient(transport);

        var initialized = await client.InitializeAsync(requestId: "initialize-legacy");

        Assert.Equal("cxshell-session-gateway", initialized.Runtime);
        using var request = JsonDocument.Parse(transport.LastRequest!);
        Assert.Equal(AgentRuntimeMethodNames.Initialize, request.RootElement.GetProperty("method").GetString());
        Assert.Empty(request.RootElement.GetProperty("params").EnumerateObject());
    }

    [Fact]
    public async Task ClientRejectsEmptyCapabilityBeforeSending()
    {
        var client = new AgentRuntimeClient(new RecordingTransport(
            "{\"requestId\":\"capability-1\",\"ok\":true,\"result\":{\"supported\":false,\"capability\":\"\"}}"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CheckCapabilityAsync("  "));
    }

    [Fact]
    public async Task ClientGeneratesRequestIdsWhenCallerOmitsOne()
    {
        var transport = new RecordingTransport((request, _) =>
        {
            using var document = JsonDocument.Parse(request);
            var requestId = document.RootElement.GetProperty("requestId").GetString();
            return $"{{\"requestId\":\"{requestId}\",\"ok\":true}}";
        });
        var client = new AgentRuntimeClient(transport);

        var response = await client.SendAsync("ping");

        Assert.True(response.Ok);
        Assert.StartsWith("cxshell-runtime-", response.RequestId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRejectsMismatchedResponsesAndSurfacesRuntimeErrors()
    {
        var mismatchTransport = new RecordingTransport(
            "{\"requestId\":\"other\",\"ok\":true}");
        var mismatchClient = new AgentRuntimeClient(mismatchTransport);

        await Assert.ThrowsAsync<AgentRuntimeProtocolException>(
            () => mismatchClient.SendAsync("ping", requestId: "expected"));

        var errorTransport = new RecordingTransport(
            "{\"requestId\":\"error-1\",\"ok\":false,\"errorCode\":\"run_rejected\",\"error\":\"Provider is unavailable.\"}");
        var errorClient = new AgentRuntimeClient(errorTransport);

        var exception = await Assert.ThrowsAsync<AgentRuntimeRequestException>(
            () => errorClient.SendResultAsync<EchoResult>("agent/run", requestId: "error-1"));

        Assert.Equal(AgentRuntimeErrorCodes.RunRejected, exception.Response.ErrorCode);
        Assert.Equal("Provider is unavailable.", exception.Message);
    }

    [Fact]
    public async Task ClientProvidesTypedRuntimeRequestCancellation()
    {
        var transport = new RecordingTransport(
            "{\"requestId\":\"cancel-client\",\"ok\":true,\"result\":{\"cancelled\":true,\"requestId\":\"target-1\"}}");
        var client = new AgentRuntimeClient(transport);

        var result = await client.CancelRequestAsync(" target-1 ", "cancel-client");

        Assert.True(result.Cancelled);
        Assert.Equal("target-1", result.RequestId);
        using var request = JsonDocument.Parse(transport.LastRequest!);
        Assert.Equal(AgentRuntimeMethodNames.RequestCancel, request.RootElement.GetProperty("method").GetString());
        Assert.Equal("target-1", request.RootElement.GetProperty("params").GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task ClientRejectsEmptyRuntimeRequestCancellationTarget()
    {
        var client = new AgentRuntimeClient(new RecordingTransport(
            "{\"requestId\":\"cancel-client\",\"ok\":true,\"result\":{\"cancelled\":false,\"requestId\":\"target-1\"}}"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CancelRequestAsync("  "));
    }

    [Fact]
    public async Task ClientBestEffortCancelsRemoteRequestWhenCallerCancels()
    {
        var transport = new CancellableTransport();
        var client = new AgentRuntimeClient(transport);
        using var cancellation = new CancellationTokenSource();

        var pending = client.SendAsync(
            "agent/block",
            requestId: "remote-request-1",
            cancellationToken: cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(
            "remote-request-1",
            await transport.CancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ClientReceivesEventsFromInProcessTransport()
    {
        using var host = new AgentRuntimeHost([new EventModule()]);
        var transport = new InProcessAgentRuntimeTransport(host);
        var client = new AgentRuntimeClient(transport);
        var received = new TaskCompletionSource<AgentRuntimeEventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.SubscribeEvents(@event =>
        {
            received.TrySetResult(@event);
        });

        var response = await client.SendAsync("agent/event", requestId: "event-request");
        var @event = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(response.Ok);
        Assert.Equal("progress", @event.EventName);
        Assert.Equal("event-module", @event.Module);
        Assert.Equal("event-request", @event.RequestId);
        Assert.Equal("agent/event", @event.Method);
        Assert.NotNull(@event.Payload);
        Assert.Equal(50, @event.Payload!.Value.GetProperty("percent").GetInt32());
    }

    [Fact]
    public void ClientRejectsEventSubscriptionWhenTransportDoesNotSupportIt()
    {
        var client = new AgentRuntimeClient(new RecordingTransport(
            "{\"requestId\":\"ping-1\",\"ok\":true}"));

        Assert.Throws<NotSupportedException>(() => client.SubscribeEvents(_ => { }));
    }

    private sealed record EchoResult(
        [property: JsonPropertyName("value")] int Value);

    private sealed record ProgressPayload(
        [property: JsonPropertyName("percent")] int Percent);

    private sealed class RecordingTransport : IAgentRuntimeTransport
    {
        private readonly Func<string, CancellationToken, string> _responseFactory;

        public RecordingTransport(string response)
            : this((_, _) => response)
        {
        }

        public RecordingTransport(Func<string, string> responseFactory)
            : this((request, _) => responseFactory(request))
        {
        }

        public RecordingTransport(Func<string, CancellationToken, string> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public string? LastRequest { get; private set; }

        public Task<string> SendAsync(
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            LastRequest = requestJson;
            return Task.FromResult(_responseFactory(requestJson, cancellationToken));
        }
    }

    private sealed class CancellableTransport :
        IAgentRuntimeTransport,
        IAgentRuntimeRequestCancellationTransport
    {
        public TaskCompletionSource<string> CancellationRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> SendAsync(
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }

        public Task RequestCancellationAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            CancellationRequested.TrySetResult(requestId);
            return Task.CompletedTask;
        }
    }

    private sealed class EventModule : IAgentRuntimeModule
    {
        public string Name => "event-module";
        public IReadOnlyCollection<string> Methods => ["agent/event"];

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            await context.EmitEventAsync("progress", new ProgressPayload(50));
            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }
    }
}
