using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

public static class AgentRuntimeMethodNames
{
    public const string Initialize = "initialize";
    public const string Ping = "ping";
    public const string RequestCancel = "runtime/cancel";
    public const string CapabilitiesCheck = "capabilities/check";
    public const string ProviderStatus = "agent/provider-status";
    public const string ProviderTest = "agent/provider-test";
    public const string ModelRequest = "agent/model-request";
    public const string ToolCatalog = "agent/tool-catalog";
    public const string SessionList = "agent/session-list";
    public const string SessionGet = "agent/session-get";
    public const string SessionCommand = "agent/session-command";
    public const string FleetDiagnostic = "agent/fleet-diagnostic";
    public const string AuditList = "agent/audit-list";
    public const string RunList = "agent/run-list";
    public const string RunStatus = "agent/run-status";
    public const string RunEvents = "agent/run-events";
    public const string RunClear = "agent/run-clear";
    public const string RuntimeInfo = "agent/runtime-info";
    public const string SessionCommandCancel = "agent/session-command-cancel";
    public const string SessionCommandApprove = "agent/session-command-approve";
    public const string SessionCommandDeny = "agent/session-command-deny";
    public const string Run = "agent/run";
    public const string Cancel = "agent/cancel";
    public const string RunAppend = "agent/run-append";
    public const string RunStop = "agent/run-stop";
    public const string RunResume = "agent/run-resume";
    public const string RunApprove = "agent/run-approve";
    public const string RunDeny = "agent/run-deny";
    public const string RunCredential = "agent/run-credential";
    public const string RunCredentialDeny = "agent/run-credential-deny";
}

public static class AgentRuntimeErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidParameters = "invalid_parameters";
    public const string UnsupportedMethod = "unsupported_method";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string SessionUnavailable = "session_unavailable";
    public const string RunRejected = "run_rejected";
    public const string RunNotFound = "run_not_found";
    public const string RequestInProgress = "request_in_progress";
    public const string Cancelled = "cancelled";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string Internal = "internal_error";
    public const string Unauthorized = "unauthorized";
    public const string ProviderError = "provider_error";
}

public static class AgentRuntimeContract
{
    public const string Protocol = "cxshell-agent";
    public const string ProtocolVersion = "1";
    public const string RuntimeVersion = "0.1";
    public const int MaximumMessageCount = 64;
    public const int MaximumMessageCharacters = 64 * 1024;
    public const int MaximumRequestIdCharacters = 128;
    public const int MaximumMethodCharacters = 128;
    public const int MaximumJsonRequestCharacters = 512 * 1024;

    public static IReadOnlyList<string> Methods { get; } =
    [
        AgentRuntimeMethodNames.Initialize,
        AgentRuntimeMethodNames.Ping,
        AgentRuntimeMethodNames.RequestCancel,
        AgentRuntimeMethodNames.CapabilitiesCheck,
        AgentRuntimeMethodNames.ProviderStatus,
        AgentRuntimeMethodNames.ProviderTest,
        AgentRuntimeMethodNames.ModelRequest,
        AgentRuntimeMethodNames.ToolCatalog,
        AgentRuntimeMethodNames.SessionList,
        AgentRuntimeMethodNames.SessionGet,
        AgentRuntimeMethodNames.SessionCommand,
        AgentRuntimeMethodNames.FleetDiagnostic,
        AgentRuntimeMethodNames.AuditList,
        AgentRuntimeMethodNames.RunList,
        AgentRuntimeMethodNames.RunStatus,
        AgentRuntimeMethodNames.RunEvents,
        AgentRuntimeMethodNames.RunClear,
        AgentRuntimeMethodNames.RuntimeInfo,
        AgentRuntimeMethodNames.SessionCommandCancel,
        AgentRuntimeMethodNames.SessionCommandApprove,
        AgentRuntimeMethodNames.SessionCommandDeny,
        AgentRuntimeMethodNames.Run,
        AgentRuntimeMethodNames.Cancel,
        AgentRuntimeMethodNames.RunAppend,
        AgentRuntimeMethodNames.RunStop,
        AgentRuntimeMethodNames.RunResume,
        AgentRuntimeMethodNames.RunApprove,
        AgentRuntimeMethodNames.RunDeny,
        AgentRuntimeMethodNames.RunCredential,
        AgentRuntimeMethodNames.RunCredentialDeny
    ];

    public static IReadOnlyList<string> Capabilities { get; } =
    [
        "agent.session.list",
        "agent.session.get",
        "agent.session.command",
        "agent.session.command.execute",
        "agent.session.command.output",
        "agent.diagnostics",
        "agent.diagnostic.run",
        "agent.diagnostic.runbook",
        "agent.fleet.diagnostic",
        "agent.audit.read",
        "agent.run.list",
        "agent.run.status",
        "agent.run.events",
        "agent.run.clear",
        "agent.session.cancel",
        "agent.session-command.cancel",
        "agent.session.command.approval",
        "runtime.request.cancel",
        "agent.provider.status",
        "agent.provider.test",
        "agent.provider.tools",
        "agent.provider.streaming",
        "agent.provider.vision",
        "agent.provider.documents",
        "agent.provider.responses",
        "agent.provider.usage",
        "agent.provider.reasoning",
        "agent.model.request",
        "agent.tool.catalog",
        "agent.run",
        "agent.run.append",
        "agent.run.stop",
        "agent.run.resume",
        "agent.run.approval",
        "agent.run.credential"
    ];
}

public sealed record AgentRuntimeResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record AgentRuntimeInitializeResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("version")] string Version)
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = AgentRuntimeContract.Protocol;

    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = AgentRuntimeContract.ProtocolVersion;

    [JsonPropertyName("methods")]
    public IReadOnlyList<string> Methods { get; init; } = [];

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record AgentRuntimePingResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("processId")] int ProcessId);

public sealed record AgentRuntimeRequestCancelResult(
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeInfoResult(
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("runtimeVersion")] string RuntimeVersion,
    [property: JsonPropertyName("methods")] IReadOnlyList<string> Methods,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("supportedProtocols")] IReadOnlyList<string> SupportedProtocols);

public sealed record AgentRuntimeCapabilityResult(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record AgentRuntimeProviderStatusResult(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("provider")] AgentProviderSnapshot Provider,
    [property: JsonPropertyName("validationStatus")] AgentProviderValidationStatus ValidationStatus,
    [property: JsonPropertyName("message")] string Message);

public sealed record AgentRuntimeProviderTestResult(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("errorType")] string? ErrorType = null)
{
    [JsonPropertyName("capabilities")]
    public AgentProviderCapabilities Capabilities { get; init; } = new();
}

public sealed record AgentRuntimeModelRequestResult(
    [property: JsonPropertyName("response")] AgentModelResponse Response);

public sealed record AgentRuntimeToolDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("unavailableReason")] string? UnavailableReason = null);

public sealed record AgentRuntimeToolCatalogResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<AgentRuntimeToolDescriptor> Tools,
    [property: JsonPropertyName("requiresApprovalForDangerousCommands")] bool RequiresApprovalForDangerousCommands,
    [property: JsonPropertyName("requiresApprovalForChangeCommands")] bool RequiresApprovalForChangeCommands = false);

public sealed record AgentRuntimeSessionListResult(
    [property: JsonPropertyName("sessions")] IReadOnlyList<AgentSessionSnapshot> Sessions);

public sealed record AgentRuntimeSessionGetResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("session")] AgentSessionSnapshot? Session,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeAuditListResult(
    [property: JsonPropertyName("entries")] IReadOnlyList<AgentAuditEntry> Entries,
    [property: JsonPropertyName("limit")] int Limit);

public sealed record AgentRuntimeRunListResult(
    [property: JsonPropertyName("runs")] IReadOnlyList<AgentRuntimeRunSnapshot> Runs,
    [property: JsonPropertyName("limit")] int Limit = AgentRunCoordinator.DefaultRunListLimit);

public sealed record AgentRuntimeRunStatusResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("run")] AgentRuntimeRunSnapshot? Run,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeRunClearResult(
    [property: JsonPropertyName("clearedCount")] int ClearedCount);

public sealed record AgentRuntimeRunEventsResult(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("events")] IReadOnlyList<AgentRuntimeStreamEnvelope> Events,
    [property: JsonPropertyName("nextSequence")] long NextSequence,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("oldestSequence")] long? OldestSequence,
    [property: JsonPropertyName("latestSequence")] long? LatestSequence,
    [property: JsonPropertyName("hasGap")] bool HasGap = false,
    [property: JsonPropertyName("status")] string Status = "running",
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset? CompletedAtUtc = null,
    [property: JsonPropertyName("endReason")] string? EndReason = null);

public sealed record AgentRuntimeSessionCommandResult(
    [property: JsonPropertyName("result")] AgentCommandResult Result);

public sealed record AgentRuntimeCancelResult(
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeRunAppendResult(
    [property: JsonPropertyName("appended")] bool Appended,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeRunStopResult(
    [property: JsonPropertyName("requested")] bool Requested,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeRunResumeResult(
    [property: JsonPropertyName("resumed")] bool Resumed,
    [property: JsonPropertyName("previousRunId")] string PreviousRunId,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeRunApprovalResult(
    [property: JsonPropertyName("decided")] bool Decided,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeRunCredentialResult(
    [property: JsonPropertyName("provided")] bool Provided,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("credentialRequestId")] string CredentialRequestId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeSessionCommandCancelResult(
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AgentRuntimeSessionCommandApprovalResult(
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("approvalToken")] string? ApprovalToken = null,
    [property: JsonPropertyName("error")] string? Error = null);

public interface IAgentRuntimeSessionAdapter
{
    Task<AgentRuntimeResponse> DispatchAsync(
        string requestId,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default);
}
