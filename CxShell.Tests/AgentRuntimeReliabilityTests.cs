using CxShell.Models;
using CxShell.Services.Agent;
using CxShell.Services.Agent.OpenCoworkRuntime;

namespace CxShell.Tests;

public sealed class AgentRuntimeReliabilityTests
{
    [Fact]
    public void SensitiveDataRedactorCoversExactKeyValueAndBearerSecrets()
    {
        var redacted = AgentSensitiveDataRedactor.Redact(
            "password=secret token: abc123 Authorization: Bearer eyJtoken",
            ["plain-secret"]);

        Assert.DoesNotContain("secret", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJtoken", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextEstimatorIncludesMessageAndContentPartOverhead()
    {
        var estimate = AgentContextEstimator.Estimate(
        [
            new AgentChatMessage(
                "user",
                "inspect this",
                ContentParts:
                [
                    AgentContentPart.ImagePart("image/png", "ZmFrZQ==", "screen.png"),
                    AgentContentPart.TextPart("document text", "notes.txt")
                ])
        ]);

        Assert.Equal(1, estimate.MessageCount);
        Assert.True(estimate.CharacterCount > "inspect this".Length);
        Assert.True(estimate.EstimatedTokens > 0);
    }

    [Fact]
    public void ContextSummaryPromptRedactsSecretsBeforeProviderCall()
    {
        var prompt = OpenCoworkRuntimeContextCompactor.BuildSummaryPrompt(
        [
            new AgentChatMessage("tool", "password=remote-secret token: abc123")
        ]);

        Assert.DoesNotContain("remote-secret", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", prompt, StringComparison.Ordinal);
        Assert.Contains("[redacted]", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCapabilitiesReflectTheConfiguredWireProtocol()
    {
        var chat = new AgentProviderSettings
        {
            Enabled = true,
            Type = AgentProviderType.OpenAiChatCompatible,
            BaseUrl = "https://provider.example",
            Model = "chat-model",
            RequiresApiKey = false
        };
        var responses = new AgentProviderSettings
        {
            Enabled = true,
            Type = AgentProviderType.OpenAiResponses,
            BaseUrl = "https://provider.example",
            Model = "responses-model",
            RequiresApiKey = false
        };

        var chatCapabilities = AgentProviderConfiguration.GetCapabilities(chat);
        var responseCapabilities = AgentProviderConfiguration.GetCapabilities(responses);

        Assert.True(chatCapabilities.SupportsTools);
        Assert.True(chatCapabilities.SupportsStreaming);
        Assert.True(chatCapabilities.SupportsVision);
        Assert.False(chatCapabilities.SupportsResponsesApi);
        Assert.True(responseCapabilities.SupportsResponsesApi);
        Assert.True(responseCapabilities.SupportsReasoning);
    }

    [Fact]
    public void ProgressOnlyEndpointAdvertisesCapturedOutput()
    {
        var session = new SessionInfo
        {
            Name = "progress-only",
            Host = "host.example",
            Username = "operator",
            Protocol = SessionProtocol.SSH
        };
        var endpoint = new AgentSessionEndpoint(
            () => AgentSessionSnapshot.FromSession(session, true),
            (_, _) => Task.CompletedTask,
            runCommand: null,
            runCommandResult: null,
            runCommandProgressResult: (_, _, _) =>
                Task.FromResult(new AgentCommandExecutionResult(true)));

        Assert.True(endpoint.SupportsCommandOutputCapture);
    }
}
