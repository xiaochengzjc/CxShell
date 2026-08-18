using System.Collections.Generic;

namespace CxShell.Models;

public enum ConnectionDiagnosticStepStatus
{
    Pending,
    Running,
    Success,
    Warning,
    Failed,
    Skipped
}

public sealed record ConnectionDiagnosticStepUpdate(
    int Index,
    string Name,
    ConnectionDiagnosticStepStatus Status,
    long? ElapsedMilliseconds = null,
    string? Detail = null);

public sealed class ConnectionDiagnosticReport
{
    public List<ConnectionDiagnosticStepUpdate> Steps { get; init; } = new();
    public bool Success { get; init; }
    public string? IssueTitle { get; init; }
    public string? IssueDescription { get; init; }
    public List<string> Suggestions { get; init; } = new();
}
