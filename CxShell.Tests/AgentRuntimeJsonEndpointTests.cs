using System.Text.Json;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeJsonEndpointTests
{
    [Fact]
    public async Task EndpointDispatchesParamsAndReturnsJsonResponse()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        var endpoint = new AgentRuntimeJsonEndpoint(host);

        var responseJson = await endpoint.DispatchAsync(
            "{\"requestId\":\"json-1\",\"method\":\"agent/echo\",\"params\":{\"value\":42}}");

        using var response = JsonDocument.Parse(responseJson);
        var root = response.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("json-1", root.GetProperty("requestId").GetString());
        Assert.Equal(42, root.GetProperty("result").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task EndpointAcceptsLegacyParametersAlias()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        var endpoint = new AgentRuntimeJsonEndpoint(host);

        var responseJson = await endpoint.DispatchAsync(
            "{\"requestId\":\"json-2\",\"method\":\"agent/echo\",\"parameters\":{\"value\":\"legacy\"}}");

        using var response = JsonDocument.Parse(responseJson);
        Assert.Equal(
            "legacy",
            response.RootElement.GetProperty("result").GetProperty("value").GetString());
    }

    [Fact]
    public async Task EndpointReturnsStableErrorsForMalformedAndOversizedRequests()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        var endpoint = new AgentRuntimeJsonEndpoint(host);

        var malformed = await endpoint.DispatchAsync("{\"requestId\":");
        var wrongRoot = await endpoint.DispatchAsync("[]");
        var oversized = await endpoint.DispatchAsync(
            new string('x', AgentRuntimeContract.MaximumJsonRequestCharacters + 1));

        Assert.Equal(AgentRuntimeErrorCodes.InvalidRequest, GetErrorCode(malformed));
        Assert.Equal(AgentRuntimeErrorCodes.InvalidRequest, GetErrorCode(wrongRoot));
        Assert.Equal(AgentRuntimeErrorCodes.InvalidRequest, GetErrorCode(oversized));
    }

    [Fact]
    public async Task EndpointDelegatesCancellationToHost()
    {
        using var host = new AgentRuntimeHost([new EchoModule()]);
        var endpoint = new AgentRuntimeJsonEndpoint(host);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var responseJson = await endpoint.DispatchAsync(
            "{\"requestId\":\"json-cancel\",\"method\":\"agent/echo\"}",
            cancellation.Token);

        Assert.Equal(AgentRuntimeErrorCodes.Cancelled, GetErrorCode(responseJson));
    }

    private static string? GetErrorCode(string responseJson)
    {
        using var response = JsonDocument.Parse(responseJson);
        return response.RootElement.GetProperty("errorCode").GetString();
    }

    private sealed class EchoModule : IAgentRuntimeModule
    {
        public string Name => "echo";
        public IReadOnlyCollection<string> Methods => ["agent/echo"];

        public Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            var value = context.Request.Parameters.ValueKind == JsonValueKind.Object &&
                        context.Request.Parameters.TryGetProperty("value", out var property)
                ? property.Clone()
                : JsonSerializer.SerializeToElement<string?>(null);
            return Task.FromResult(new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true,
                Result = JsonSerializer.SerializeToElement(new { value })
            });
        }
    }
}
