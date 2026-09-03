using System.Text.Json;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentToolResultEnvelopeTests
{
    [Fact]
    public void MergeDoesNotClaimRemoteCompletionForAGenericTool()
    {
        var json = AgentToolResultEnvelope.Merge(
            "{\"items\":[1,2]}",
            success: true,
            sessionId: Guid.NewGuid().ToString("D"),
            durationMs: 42);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("outcomeCertain").GetBoolean());
        Assert.False(root.GetProperty("remoteCompletionConfirmed").GetBoolean());
        Assert.False(root.GetProperty("retrySafe").GetBoolean());
        Assert.Equal(
            AgentCommandVerificationState.Unknown.ToString(),
            root.GetProperty("verification").GetProperty("state").GetString());
    }

    [Fact]
    public void MergePreservesTheCommandExecutionContract()
    {
        var json = AgentToolResultEnvelope.Merge(
            "{\"executionState\":\"Dispatched\",\"outcomeCertain\":false,\"remoteCompletionConfirmed\":false,\"retrySafe\":false}",
            success: true,
            sessionId: Guid.NewGuid().ToString("D"),
            durationMs: 9);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Dispatched", root.GetProperty("executionState").GetString());
        Assert.False(root.GetProperty("outcomeCertain").GetBoolean());
        Assert.False(root.GetProperty("remoteCompletionConfirmed").GetBoolean());
        Assert.False(root.GetProperty("retrySafe").GetBoolean());
    }
}
