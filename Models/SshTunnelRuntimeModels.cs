using System;

namespace CxShell.Models;

public enum SshTunnelRuntimeStatus
{
    Stopped,
    Running,
    Error
}

public readonly record struct SshTunnelActivitySnapshot(
    long ConnectionCount,
    DateTimeOffset? LastActivityAt,
    string? LastOriginator)
{
    public static SshTunnelActivitySnapshot Empty => new(0, null, null);

    public SshTunnelActivitySnapshot RecordConnection(
        DateTimeOffset activityAt,
        string? originator)
    {
        return new SshTunnelActivitySnapshot(
            checked(ConnectionCount + 1),
            activityAt,
            string.IsNullOrWhiteSpace(originator) ? null : originator.Trim());
    }
}

public sealed record SshTunnelRuntimeSnapshot(
    Guid Id,
    SshTunnelRuleType Type,
    string Description,
    string ListenHost,
    int ListenPort,
    string DestinationHost,
    int DestinationPort,
    SshTunnelRuntimeStatus Status,
    DateTimeOffset? StartedAt,
    string? LastError,
    SshTunnelActivitySnapshot Activity = default);

public sealed record SshTunnelOperationResult(bool Success, string? ErrorMessage)
{
    public static SshTunnelOperationResult Completed() => new(true, null);

    public static SshTunnelOperationResult Failed(string message) => new(false, message);
}
