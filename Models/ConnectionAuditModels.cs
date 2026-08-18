using System;
using System.Text.Json.Serialization;

namespace CxShell.Models;

public enum ConnectionAuditEventType
{
    ConnectStarted,
    Connected,
    Failed,
    Disconnected,
    TabClosed
}

public sealed class ConnectionAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid SessionId { get; set; }
    public string SessionName { get; set; } = string.Empty;
    public SessionProtocol Protocol { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public ConnectionAuditEventType EventType { get; set; }
    public string Detail { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTimeOffset LocalTimestamp => TimestampUtc.ToLocalTime();
}
