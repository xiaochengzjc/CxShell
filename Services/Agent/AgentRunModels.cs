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

public sealed record AgentContextEstimate(
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("characterCount")] int CharacterCount,
    [property: JsonPropertyName("estimatedTokens")] int EstimatedTokens);

public sealed record AgentRunStep(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("status")] string Status = "pending",
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset? StartedAtUtc = null,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null);

public static class AgentRunStepStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Waiting = "waiting";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string? status)
        => status is Completed or Failed or Cancelled;
}

public sealed record AgentCommandProgress(
    Guid RequestId,
    Guid SessionId,
    string Text,
    bool IsError = false,
    long? ElapsedMs = null);

/// <summary>
/// Bounded, non-sensitive progress metadata used to continue an interrupted
/// run. It deliberately contains no command text, tool output, or secrets.
/// </summary>
public sealed record AgentRunCheckpoint(
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("modelRequestCount")] int ModelRequestCount = 0,
    [property: JsonPropertyName("toolCallCount")] int ToolCallCount = 0,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc = default,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("toolExecutionState")] string? ToolExecutionState = null,
    [property: JsonPropertyName("toolOutcomeCertain")] bool ToolOutcomeCertain = false,
    [property: JsonPropertyName("toolRemoteCompletionConfirmed")] bool ToolRemoteCompletionConfirmed = false,
    [property: JsonPropertyName("toolRetrySafe")] bool ToolRetrySafe = false)
{
    [JsonPropertyName("context")]
    public AgentContextEstimate? Context { get; init; }

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
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc = null,
    [property: JsonPropertyName("checkpoint")] AgentRunCheckpoint? Checkpoint = null)
{
    [JsonPropertyName("context")]
    public AgentContextEstimate? Context { get; init; }
}

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
    [property: JsonPropertyName("canResume")] bool CanResume = false,
    [property: JsonPropertyName("checkpoint")] AgentRunCheckpoint? Checkpoint = null,
    [property: JsonPropertyName("phase")] string Phase = "run",
    [property: JsonPropertyName("pauseReason")] string? PauseReason = null,
    [property: JsonPropertyName("requiresUserAction")] bool RequiresUserAction = false)
{
    [JsonPropertyName("steps")]
    public IReadOnlyList<AgentRunStep> Steps { get; init; } = [];
}

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
    [property: JsonPropertyName("sessionName")] string? SessionName = null,
    [property: JsonPropertyName("checkpoint")] AgentRunCheckpoint? Checkpoint = null,
    [property: JsonPropertyName("phase")] string? Phase = null,
    [property: JsonPropertyName("pauseReason")] string? PauseReason = null,
    [property: JsonPropertyName("requiresUserAction")] bool RequiresUserAction = false)
{
    [JsonPropertyName("stream")]
    public string? Stream { get; init; }

    [JsonPropertyName("step")]
    public AgentRunStep? Step { get; init; }

    [JsonPropertyName("stepIndex")]
    public int? StepIndex { get; init; }

    [JsonPropertyName("stepCount")]
    public int? StepCount { get; init; }

    [JsonPropertyName("sessionHost")]
    public string? SessionHost { get; init; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    [JsonPropertyName("credentialInputType")]
    public string? CredentialInputType { get; init; }

    [JsonPropertyName("credentialMasked")]
    public bool? CredentialMasked { get; init; }

    [JsonPropertyName("credentialCanRemember")]
    public bool? CredentialCanRemember { get; init; }

    [JsonPropertyName("credentialPurpose")]
    public string? CredentialPurpose { get; init; }

    [JsonPropertyName("context")]
    public AgentContextEstimate? Context { get; init; }

    [JsonPropertyName("commandRequestId")]
    public string? CommandRequestId { get; init; }
}

public static class AgentRunStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string WaitingForInput = "waiting_for_input";
    public const string PendingApproval = "pending_approval";
    public const string Stopping = "stopping";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Stopped = "stopped";
    public const string TimedOut = "timed_out";
    public const string Interrupted = "interrupted";
    public const string Failed = "failed";

    public static bool IsActive(string? status)
        => Normalize(status) is Starting or Running or WaitingForInput or PendingApproval or Stopping;

    public static bool IsWaiting(string? status)
        => Normalize(status) is WaitingForInput or PendingApproval;

    public static bool IsFailure(string? status)
        => Normalize(status) is Failed or TimedOut;

    private static string Normalize(string? status)
        => status?.Trim().ToLowerInvariant() ?? string.Empty;
}

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
