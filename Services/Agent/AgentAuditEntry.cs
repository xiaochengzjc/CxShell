namespace CxShell.Services.Agent;

/// <summary>
/// In-memory audit information for agent actions. The command text is never
/// persisted; only its length and a one-way fingerprint are retained.
/// </summary>
public sealed record AgentAuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RequestId { get; init; }
    public Guid SessionId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public AgentCommandStatus Status { get; init; }
    public AgentCommandRisk Risk { get; init; }
    public AgentPermissionDecision? PermissionDecision { get; init; }
    public bool ApprovalRequired { get; init; }
    public bool ApprovalGranted { get; init; }
    public int CommandLength { get; init; }
    public string CommandFingerprint { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
