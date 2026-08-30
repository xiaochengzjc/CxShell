using CxShell.Services.Agent;
using CxShell.Services.Agent.OpenCoworkRuntime;

namespace CxShell.Tests;

public sealed class OpenCoworkRuntimeContextCompactorTests
{
    [Fact]
    public async Task CompressionPreservesSystemPromptAndSafeToolBoundary()
    {
        var conversation = new List<AgentChatMessage>
        {
            new("system", "You are an operations assistant."),
            new("user", "Install the Java runtime and verify it."),
            new(
                "assistant",
                "",
                ToolCalls:
                [new AgentToolCall("call-1", "session_command", "{\"command\":\"download java\"}")]),
            new("tool", "download failed", ToolCallId: "call-1"),
            new("user", "Try the configured mirror."),
            new(
                "assistant",
                "",
                ToolCalls:
                [new AgentToolCall("call-2", "session_command", "{\"command\":\"install java\"}")]),
            new("tool", "install completed", ToolCallId: "call-2"),
            new("user", "Verify the version."),
            new("assistant", "Checking the version now."),
            new("tool", "java version 21", ToolCallId: "call-3")
        };
        var compactor = new OpenCoworkRuntimeContextCompactor(
            messageLimit: 6,
            characterLimit: 8 * 1024,
            preserveRecentMessages: 2);

        var result = await compactor.CompressIfNeededAsync(
            conversation,
            (_, _) => Task.FromResult<string?>("The Java runtime was installed after switching to a mirror."));

        Assert.True(result.IsCompressed);
        Assert.False(result.UsedFallback);
        Assert.Equal(10, result.OriginalMessageCount);
        Assert.Equal("system", result.Messages[0].Role);
        Assert.Contains(
            "The Java runtime was installed",
            result.Messages[2].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Messages.Skip(1),
            message => message.Role == "tool" &&
                       (message.ToolCallId == "call-1" || message.ToolCallId == "call-2"));
        Assert.Equal("java version 21", result.Messages[^1].Content);
    }

    [Fact]
    public async Task CompressionFallsBackLocallyWhenSummarizerFails()
    {
        var conversation = new List<AgentChatMessage>
        {
            new("system", "system"),
            new("user", "Install Java on the server."),
            new("assistant", "I will inspect the package manager."),
            new("tool", "github connection failed", ToolCallId: "call-1"),
            new("user", "Use a Microsoft mirror instead."),
            new("assistant", "The download is still running."),
            new("user", "Report the result when it finishes.")
        };
        var compactor = new OpenCoworkRuntimeContextCompactor(
            messageLimit: 5,
            characterLimit: 8 * 1024,
            preserveRecentMessages: 2);

        var result = await compactor.CompressIfNeededAsync(
            conversation,
            (_, _) => Task.FromException<string?>(new InvalidOperationException("provider unavailable")));

        Assert.True(result.IsCompressed);
        Assert.True(result.UsedFallback);
        Assert.Contains(
            "Install Java on the server",
            result.Messages[2].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "github connection failed",
            result.Messages[2].Content,
            StringComparison.Ordinal);
    }
}
