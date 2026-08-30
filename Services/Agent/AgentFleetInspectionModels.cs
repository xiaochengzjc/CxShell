namespace CxShell.Services.Agent;

public sealed record AgentFleetInspectionItem(
    Guid SessionId,
    string Name,
    string Host,
    string Platform,
    string Status,
    string Message,
    bool RemoteCompletionConfirmed,
    string? Output = null);

public sealed record AgentFleetInspectionResult(
    string Scope,
    int TargetCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<AgentFleetInspectionItem> Results);
