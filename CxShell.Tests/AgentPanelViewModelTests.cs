using AtomUI.Controls;
using AtomUI.Controls.Primitives;
using AtomUI.Desktop.Controls;
using CxShell.Services.Agent;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class AgentPanelViewModelTests
{
    [Fact]
    public void AssistantMessage_AppendsTextWithoutExceedingTheConfiguredLimit()
    {
        var message = AgentPanelMessageViewModel.Assistant("1234");

        message.AppendText("567890", 8);

        Assert.Equal("12345678\n[...]", message.Content);
        Assert.NotNull(message.MarkdownBuilder);
        Assert.Equal(message.Content, message.MarkdownBuilder!.ToString());
    }

    [Fact]
    public void MessageKind_ExposesOnlyItsOwnPresentationState()
    {
        var user = AgentPanelMessageViewModel.User("check");
        var tool = AgentPanelMessageViewModel.Tool("uname -a");
        var error = AgentPanelMessageViewModel.Error("failed");

        Assert.True(user.IsUser);
        Assert.False(user.IsTool);
        Assert.True(tool.IsTool);
        Assert.False(tool.IsError);
        Assert.True(error.IsError);
        Assert.False(error.IsAssistant);
    }

    [Fact]
    public void ToolDetailsAreCollapsedByDefault()
    {
        var tool = AgentPanelMessageViewModel.Tool("command output");

        Assert.True(tool.IsTool);
        Assert.False(tool.IsToolDetailsExpanded);
        Assert.False(string.IsNullOrWhiteSpace(tool.ToolDetailsButtonText));
    }

    [Fact]
    public void RunSummaryMetricsCanBeUpdatedFromRuntimeSnapshot()
    {
        var summary = AgentPanelMessageViewModel.Summary(
            "run-1",
            "test-server",
            "Completed",
            "00:01",
            1,
            1,
            "done");

        summary.UpdateSummaryMetrics(new AgentRuntimeRunSnapshot(
            "run-1",
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            "completed",
            DurationMs: 3725,
            ToolCallCount: 3,
            ModelRequestCount: 4));

        Assert.Equal(3, summary.SummaryToolCallCount);
        Assert.Equal(4, summary.SummaryModelRequestCount);
        Assert.Equal("00:03", summary.SummaryDurationText);
        Assert.NotNull(summary.SummaryMarkdownBuilder);
        Assert.Equal(summary.SummaryResultText, summary.SummaryMarkdownBuilder!.ToString());
    }

    [Fact]
    public void FleetToolResultIsFormattedAsCompactSummary()
    {
        var value = """
        {
          "fleet": true,
          "diagnostic": "disk",
          "targetCount": 2,
          "successCount": 1,
          "failureCount": 1,
          "results": [
            { "name": "server-a", "host": "10.0.0.1", "platform": "Linux/Unix", "status": "Sent", "output": "large output" },
            { "name": "server-b", "host": "10.0.0.2", "platform": "Windows", "status": "Failed", "output": "failure details" }
          ]
        }
        """;

        var formatted = AgentPanelViewModel.FormatToolResult(value);

        Assert.Contains("2 targets, 1 succeeded, 1 failed.", formatted, StringComparison.Ordinal);
        Assert.Contains("server-a (10.0.0.1) | Linux/Unix | Sent", formatted, StringComparison.Ordinal);
        Assert.Contains("server-b (10.0.0.2) | Windows | Failed", formatted, StringComparison.Ordinal);
        Assert.Contains("Detailed output is available in each target Terminal.", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("large output", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionSwitchKeepsConversationStateSeparated()
    {
        using var panel = new AgentPanelViewModel(new TestRuntimeClient());
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var firstOption = new SelectOption
        {
            Header = "first",
            Content = firstSession.ToString("D")
        };
        var secondOption = new SelectOption
        {
            Header = "second",
            Content = secondSession.ToString("D")
        };

        panel.SelectedSessionOption = firstOption;
        panel.Messages.Add(AgentPanelMessageViewModel.User("first session"));
        panel.SelectedSessionOption = secondOption;
        panel.Messages.Add(AgentPanelMessageViewModel.User("second session"));

        panel.SelectedSessionOption = firstOption;

        var message = Assert.Single(panel.Messages);
        Assert.Equal("first session", message.Content);
    }

    [Fact]
    public void RuntimeIsRequiredBeforeAnAgentRunCanStart()
    {
        using var panel = new AgentPanelViewModel(new TestRuntimeClient());

        Assert.False(panel.IsRuntimeReady);
        Assert.False(panel.CanRun());
    }

    [Fact]
    public void FindActiveRunReturnsTheNewestRunningRunForTheSelectedSession()
    {
        var sessionId = Guid.NewGuid();
        var selected = AgentPanelViewModel.FindActiveRun(
            [
                new AgentRuntimeRunSnapshot(
                    "completed-run",
                    sessionId.ToString("D"),
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    "completed"),
                new AgentRuntimeRunSnapshot(
                    "older-running-run",
                    sessionId.ToString("D"),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    "running"),
                new AgentRuntimeRunSnapshot(
                    "newer-running-run",
                    sessionId.ToString("D"),
                    DateTimeOffset.UtcNow,
                    "running"),
                new AgentRuntimeRunSnapshot(
                    "other-session-run",
                    Guid.NewGuid().ToString("D"),
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    "running")
            ],
            sessionId);

        Assert.NotNull(selected);
        Assert.Equal("newer-running-run", selected.RunId);
        Assert.Null(AgentPanelViewModel.FindActiveRun([], Guid.NewGuid()));
    }

    private sealed class TestRuntimeClient : IAgentRuntimeClient
    {
        public Task<AgentRuntimeInitializeResult> InitializeAsync(
            string? protocol = null,
            string? protocolVersion = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRuntimeInfoResult> GetRuntimeInfoAsync(
            string? requestId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRuntimeCapabilityResult> CheckCapabilityAsync(
            string capability,
            string? requestId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRuntimeResponse> SendAsync(
            string method,
            object? parameters = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T> SendResultAsync<T>(
            string method,
            object? parameters = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AgentRuntimeRequestCancelResult> CancelRequestAsync(
            string requestId,
            string? cancellationRequestId = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer)
            => new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
