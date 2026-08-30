using CxShell.Services.Agent;
using CxShell.Models;

namespace CxShell.Tests;

public sealed class AgentProviderStabilityTests
{
    [Theory]
    [InlineData(401, AgentProviderErrorKind.Authentication, false)]
    [InlineData(403, AgentProviderErrorKind.Authentication, false)]
    [InlineData(408, AgentProviderErrorKind.Timeout, true)]
    [InlineData(429, AgentProviderErrorKind.RateLimited, true)]
    [InlineData(500, AgentProviderErrorKind.Server, true)]
    [InlineData(503, AgentProviderErrorKind.Server, true)]
    [InlineData(400, AgentProviderErrorKind.Request, false)]
    public void HttpStatusIsClassifiedForSafeRetryDecisions(
        int statusCode,
        AgentProviderErrorKind expectedKind,
        bool expectedRetryable)
    {
        var exception = AgentProviderException.FromStatusCode(
            statusCode,
            "https://provider.example/v1/chat/completions?secret=hidden");

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedRetryable, exception.Retryable);
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.DoesNotContain("secret", exception.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NetworkAndProtocolFailuresAreDistinct()
    {
        var network = AgentProviderException.Network(new IOException("socket closed"));
        var protocol = AgentProviderException.Protocol("Provider returned an invalid response.");

        Assert.Equal(AgentProviderErrorKind.Network, network.Kind);
        Assert.True(network.Retryable);
        Assert.Equal(AgentProviderErrorKind.Protocol, protocol.Kind);
        Assert.False(protocol.Retryable);
    }

    [Fact]
    public async Task CoordinatorRetriesTransientProviderFailuresAndRecordsTheKind()
    {
        var session = AgentSessionSnapshot.FromSession(
            new SessionInfo
            {
                Name = "Provider retry test",
                Host = "provider-test.example",
                Protocol = SessionProtocol.SSH
            },
            isConnected: true);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => session,
                    (_, _) => Task.CompletedTask)
            ]));
        var provider = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "test-provider",
            BaseUrl = "https://provider.example/v1",
            Model = "test-model",
            RequiresApiKey = false
        };
        var attempts = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new RetryModelClient(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw AgentProviderException.FromStatusCode(
                        503,
                        "https://provider.example/v1/chat/completions");
                }

                return new AgentModelResponse("ready", provider.Model, provider.BuiltinId);
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<AgentRuntimeStreamEvent>();
        using var subscription = coordinator.Subscribe(envelope =>
        {
            lock (events)
                events.AddRange(envelope.Events);
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "provider-retry-run",
            SessionId = session.SessionId,
            Messages = [new AgentChatMessage("user", "check")]
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, attempts);
        var retry = Assert.Single(events, @event => @event.Type == "request_retry");
        Assert.Equal(nameof(AgentProviderErrorKind.Server), retry.ErrorType);
        Assert.Equal(503, retry.StatusCode);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)!.Status);
        Assert.Equal(1, coordinator.GetRun(start.RunId)!.ModelRequestCount);
    }

    private sealed class RetryModelClient : IAgentModelClient
    {
        private readonly Func<AgentModelResponse> _response;

        public RetryModelClient(Func<AgentModelResponse> response)
        {
            _response = response;
        }

        public Task<AgentModelResponse> CompleteAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response());
        }
    }
}
