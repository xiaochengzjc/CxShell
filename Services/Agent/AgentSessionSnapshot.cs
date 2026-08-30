using CxShell.Models;

namespace CxShell.Services.Agent;

/// <summary>
/// The session metadata exposed to an agent. Secrets and connection objects
/// deliberately do not belong in this DTO.
/// </summary>
public sealed record AgentSessionSnapshot
{
    public Guid SessionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public SessionProtocol Protocol { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Username { get; init; } = string.Empty;
    public bool IsConnected { get; init; }
    public string Platform { get; init; } = "Unknown";
    public bool CanExecuteCommands => IsConnected && Protocol == SessionProtocol.SSH;

    public static AgentSessionSnapshot FromSession(
        SessionInfo session,
        bool isConnected,
        string platform = "Unknown")
    {
        ArgumentNullException.ThrowIfNull(session);

        return new AgentSessionSnapshot
        {
            SessionId = session.Id,
            Name = session.Name ?? string.Empty,
            Protocol = session.Protocol,
            Host = session.Host ?? string.Empty,
            Port = session.Port,
            Username = session.Username ?? string.Empty,
            IsConnected = isConnected,
            Platform = string.IsNullOrWhiteSpace(platform) ? "Unknown" : platform
        };
    }
}
