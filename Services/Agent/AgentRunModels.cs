using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

public sealed record AgentRunRequest
{
    public string? RunId { get; init; }
    public Guid SessionId { get; init; }
    public IReadOnlyList<AgentChatMessage> Messages { get; init; } = [];
    public string? Model { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public TimeSpan Timeout { get; init; } = AgentRunCoordinator.DefaultRunTimeout;
}

/// <summary>
/// Safe metadata required to offer a manual retry after the application was
/// closed. It intentionally contains no provider credentials or tool output.
/// </summary>
public sealed record AgentRunRecoveryState(
    [property: JsonPropertyName("snapshot")] AgentRuntimeRunSnapshot Snapshot,
    [property: JsonPropertyName("messages")] IReadOnlyList<AgentChatMessage> Messages,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("maxTokens")] int? MaxTokens = null,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs = 0,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc = null);

public sealed record AgentRunStartResult(
    bool Started,
    string RunId,
    string? Error = null);

public sealed record AgentRunCancellationResult(
    bool Cancelled,
    string RunId,
    string? Error = null);

public sealed record AgentRunAppendMessagesResult(
    bool Appended,
    string RunId,
    int MessageCount,
    string? Error = null);

public sealed record AgentRunStopResult(
    bool Requested,
    string RunId,
    string? Error = null);

public sealed record AgentRunResumeResult(
    bool Resumed,
    string PreviousRunId,
    string RunId,
    Guid SessionId,
    string? Error = null);

public sealed record AgentRunApprovalResult(
    bool Decided,
    bool Approved,
    string RunId,
    string ToolCallId,
    string? Error = null);

public sealed record AgentRunCredentialResult(
    bool Provided,
    string RunId,
    string CredentialRequestId,
    string? Error = null);

public sealed record AgentRuntimeRunResult(
    [property: JsonPropertyName("started")] bool Started,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record AgentRuntimeRunSnapshot(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("status")] string Status = "running",
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc = null,
    [property: JsonPropertyName("endReason")] string? EndReason = null,
    [property: JsonPropertyName("eventCount")] long EventCount = 0,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("promptPreview")] string? PromptPreview = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("errorType")] string? ErrorType = null,
    [property: JsonPropertyName("toolCallCount")] int ToolCallCount = 0,
    [property: JsonPropertyName("modelRequestCount")] int ModelRequestCount = 0,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("lastEventAtUtc")] DateTimeOffset? LastEventAtUtc = null,
    [property: JsonPropertyName("canResume")] bool CanResume = false);

public sealed record AgentRuntimeStreamEnvelope(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("events")] IReadOnlyList<AgentRuntimeStreamEvent> Events);

public sealed record AgentRuntimeStreamEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("errorType")] string? ErrorType = null,
    [property: JsonPropertyName("details")] string? Details = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("inputTokens")] int? InputTokens = null,
    [property: JsonPropertyName("outputTokens")] int? OutputTokens = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null,
    [property: JsonPropertyName("credentialRequestId")] string? CredentialRequestId = null,
    [property: JsonPropertyName("credentialKind")] string? CredentialKind = null,
    [property: JsonPropertyName("credentialPrompt")] string? CredentialPrompt = null,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("input")] string? Input = null,
    [property: JsonPropertyName("result")] string? Result = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("attempt")] int? Attempt = null,
    [property: JsonPropertyName("maxAttempts")] int? MaxAttempts = null,
    [property: JsonPropertyName("delayMs")] int? DelayMs = null,
    [property: JsonPropertyName("statusCode")] int? StatusCode = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("elapsedMs")] long? ElapsedMs = null,
    [property: JsonPropertyName("risk")] string? Risk = null,
    [property: JsonPropertyName("timeoutMs")] int? TimeoutMs = null,
    [property: JsonPropertyName("sessionName")] string? SessionName = null);

public interface IAgentRunCoordinator
{
    AgentRunStartResult Start(AgentRunRequest request);
    AgentRunCancellationResult Cancel(string runId);
    AgentRunAppendMessagesResult AppendMessages(
        string runId,
        IReadOnlyList<AgentChatMessage> messages);
    AgentRunStopResult RequestStop(string runId);
    AgentRunResumeResult Resume(string runId)
        => new(false, runId?.Trim() ?? string.Empty, string.Empty, Guid.Empty, "Run continuation is not supported.");
    AgentRunApprovalResult Approve(string runId, string toolCallId);
    AgentRunApprovalResult Deny(string runId, string toolCallId);
    AgentRunCredentialResult ProvideCredential(
        string runId,
        string credentialRequestId,
        string value,
        bool rememberForRun)
        => new(false, runId?.Trim() ?? string.Empty, credentialRequestId?.Trim() ?? string.Empty, "Credential input is not supported.");
    AgentRunCredentialResult DenyCredential(string runId, string credentialRequestId)
        => new(false, runId?.Trim() ?? string.Empty, credentialRequestId?.Trim() ?? string.Empty, "Credential input is not supported.");
    IReadOnlyList<AgentRuntimeRunSnapshot> GetActiveRuns();
    IReadOnlyList<AgentRuntimeRunSnapshot> GetRecentRuns(
        int limit = AgentRunCoordinator.DefaultRunListLimit);
    AgentRuntimeRunSnapshot? GetRun(string runId);
    int ClearCompletedRuns();
    AgentRuntimeRunEventsResult? ReadEvents(
        string runId,
        long afterSequence = 0,
        int limit = AgentRunCoordinator.DefaultEventReadLimit);
    IDisposable Subscribe(Action<AgentRuntimeStreamEnvelope> observer);
}
