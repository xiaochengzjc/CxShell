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
    public bool RemoteCompletionConfirmed { get; init; }
    public bool ApprovalRequired { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public int? ExitCode { get; init; }

    public bool IsSuccess => Status == AgentCommandStatus.Sent;
    public bool IsOutcomeCertain => ExecutionState is
        AgentCommandExecutionState.Completed or AgentCommandExecutionState.Dispatched ||
        (ExecutionState == AgentCommandExecutionState.Failed && RemoteCompletionConfirmed);
    public bool IsRetrySafe => ExecutionState is
        AgentCommandExecutionState.Denied or AgentCommandExecutionState.Failed;
}
