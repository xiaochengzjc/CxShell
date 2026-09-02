using System.Text.Json;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeProviderTests
{
    [Fact]
    public async Task ProviderStatusExposesConfigurationWithoutTheKey()
    {
        var settings = AgentProviderPresets.CreateRoutinPlan();
        AgentProviderConfiguration.SetApiKey(settings, "plan-secret-key");
        settings.Enabled = true;
        using var gateway = CreateGateway();
        var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => settings,
            new StubAgentModelClient());

        var response = await Dispatch(adapter, "provider-1", AgentRuntimeMethodNames.ProviderStatus);

        Assert.True(response.Ok);
        Assert.True(response.Result!.Value.GetProperty("configured").GetBoolean());
        Assert.True(response.Result.Value.GetProperty("provider").GetProperty("hasApiKey").GetBoolean());
        Assert.DoesNotContain("plan-secret-key", response.Result.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelRequestUsesProviderClientAndReturnsResponse()
    {
        var settings = AgentProviderPresets.CreateRoutinPlan();
        AgentProviderConfiguration.SetApiKey(settings, "plan-secret-key");
        settings.Enabled = true;
        using var gateway = CreateGateway();
        var modelClient = new StubAgentModelClient();
        var adapter = new AgentRuntimeSessionAdapter(gateway, () => settings, modelClient);

        var response = await Dispatch(
            adapter,
            "model-1",
            AgentRuntimeMethodNames.ModelRequest,
            new
            {
                messages = new[] { new { role = "user", content = "check the server" } }
            });

        Assert.True(response.Ok);
        Assert.Equal("stub response", response.Result!.Value
            .GetProperty("response").GetProperty("text").GetString());
        Assert.Single(modelClient.LastRequest!.Messages);
        Assert.Equal("check the server", modelClient.LastRequest.Messages[0].Content);
    }

    [Fact]
    public async Task ModelRequestIsRejectedWhenProviderIsNotConfigured()
    {
        using var gateway = CreateGateway();
        var settings = AgentProviderPresets.CreateRoutinPlan();
        var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => settings,
            new StubAgentModelClient());

        var response = await Dispatch(
            adapter,
            "model-2",
            AgentRuntimeMethodNames.ModelRequest,
            new
            {
                messages = new[] { new { role = "user", content = "hello" } }
            });

        Assert.False(response.Ok);
        Assert.Contains("disabled", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderTestReturnsReachabilityAndDurationWithoutTheKey()
    {
        var settings = AgentProviderPresets.CreateRoutinPlan();
        AgentProviderConfiguration.SetApiKey(settings, "plan-secret-key");
        settings.Enabled = true;
        using var gateway = CreateGateway();
        var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => settings,
            new StubAgentModelClient());

        var response = await Dispatch(adapter, "provider-test-1", AgentRuntimeMethodNames.ProviderTest);

        Assert.True(response.Ok);
        var result = response.Result!.Value;
        Assert.True(result.GetProperty("reachable").GetBoolean());
        Assert.Equal(settings.Model, result.GetProperty("model").GetString());
        Assert.True(result.GetProperty("durationMs").GetInt64() >= 0);
        Assert.DoesNotContain("plan-secret-key", response.Result.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderTestReturnsStructuredFailureWhenConfigurationIsInvalid()
    {
        using var gateway = CreateGateway();
        var settings = AgentProviderPresets.CreateRoutinPlan();
        var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => settings,
            new StubAgentModelClient());

        var response = await Dispatch(adapter, "provider-test-2", AgentRuntimeMethodNames.ProviderTest);

        Assert.True(response.Ok);
        Assert.False(response.Result!.Value.GetProperty("reachable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(response.Result.Value.GetProperty("message").GetString()));
        Assert.Equal(
            AgentProviderValidationStatus.Disabled.ToString(),
            response.Result.Value.GetProperty("errorType").GetString());
    }

    private static Task<AgentRuntimeResponse> Dispatch(
        IAgentRuntimeSessionAdapter adapter,
        string requestId,
        string method,
        object? parameters = null)
    {
        var element = parameters == null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(parameters);
        return adapter.DispatchAsync(requestId, method, element);
    }

    private static AgentSessionGateway CreateGateway()
    {
        var host = new DelegateAgentSessionHost(() => []);
        return new AgentSessionGateway(host);
    }

    private sealed class StubAgentModelClient : IAgentModelClient
    {
        public AgentModelRequest? LastRequest { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentModelResponse(
                "stub response",
                provider.Model,
                provider.BuiltinId));
        }
    }
}
