namespace CxShell.Services.Agent;

public enum AgentCommandExecutionState
{
    Queued,
    Executing,
    Completed,
    Dispatched,
    Unknown,
    Cancelled,
    TimedOut,
    Denied,
    Failed
}

public enum AgentCommandStatus
{
    Sent,
    Cancelled,
    TimedOut,
    Denied,
    InvalidRequest,
    SessionNotFound,
    SessionNotConnected,
    UnsupportedProtocol,
    Failed
}

public enum AgentCommandErrorType
{
    None,
    InvalidRequest,
    PermissionDenied,
    SessionUnavailable,
    UnsupportedProtocol,
    Transport,
    Timeout,
    Cancelled,
    RemoteExitCode,
    Unknown
}

/// <summary>
/// Result of dispatching or executing a command through the session gateway.
/// A Sent result can mean either that input was queued or that the remote exec
/// channel completed successfully, depending on the endpoint capabilities.
/// </summary>
public sealed record AgentCommandResult
{
    public Guid RequestId { get; init; }
    public Guid SessionId { get; init; }
    public AgentCommandStatus Status { get; init; }
    public AgentCommandExecutionState ExecutionState { get; init; } = AgentCommandExecutionState.Unknown;
    public AgentCommandRisk Risk { get; init; } = AgentCommandRisk.ReadOnly;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public bool RemoteCompletionConfirmed { get; init; }
    public bool ApprovalRequired { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public int? ExitCode { get; init; }
    public AgentCommandErrorType ErrorType { get; init; }

    public bool IsSuccess => Status == AgentCommandStatus.Sent;
    /// <summary>
    /// Indicates whether the remote endpoint provided a definitive outcome.
    /// A dispatched terminal input is only delivery confirmation: the shell may
    /// still be running the command, waiting for input, or fail before producing
    /// a prompt. Callers must not summarize it as remotely completed.
    /// </summary>
    public bool IsOutcomeCertain =>
        ExecutionState == AgentCommandExecutionState.Completed ||
        (ExecutionState == AgentCommandExecutionState.Failed && RemoteCompletionConfirmed);
    public bool IsRetrySafe => ExecutionState is
        AgentCommandExecutionState.Denied or AgentCommandExecutionState.Failed;
    public bool TimedOut => Status == AgentCommandStatus.TimedOut;
    public bool WasCancelled => Status == AgentCommandStatus.Cancelled;
}
