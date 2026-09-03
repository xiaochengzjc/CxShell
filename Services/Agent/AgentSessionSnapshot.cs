using CxShell.Models;

namespace CxShell.Services.Agent;

/// <summary>
/// The session metadata exposed to an agent. Secrets and connection objects
/// deliberately do not belong in this DTO.
/// </summary>
public sealed record AgentSessionSnapshot
{
    public Guid SessionId { get; init; }
    /// <summary>The id of the saved CxShell configuration, when available.</summary>
    public Guid? SavedSessionId { get; init; }
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
        => FromSession(session, session.Id, isConnected, platform);

    public static AgentSessionSnapshot FromSession(
        SessionInfo session,
        Guid runtimeSessionId,
        bool isConnected,
        string platform = "Unknown")
    {
        ArgumentNullException.ThrowIfNull(session);
        if (runtimeSessionId == Guid.Empty)
            throw new ArgumentException("A runtime session id is required.", nameof(runtimeSessionId));

        return new AgentSessionSnapshot
        {
            SessionId = runtimeSessionId,
            SavedSessionId = session.Id,
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
