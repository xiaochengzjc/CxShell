namespace CxShell.Services.Agent;

public sealed record AgentGatewayCapabilities
{
    public bool SupportsSessionDiscovery { get; init; } = true;
    public bool SupportsTerminalCommandDispatch { get; init; } = true;
    public bool SupportsCommandOutputCapture { get; init; }
    public bool SupportsReadOnlyDiagnostics { get; init; }
    public bool AllowsCommandExecution { get; init; } = true;
    public string PermissionMode { get; init; } = AgentPermissionPolicy.RiskBasedApprovalMode;
    public bool RequiresApprovalForDangerousCommands { get; init; } = true;
    public bool RequiresApprovalForChangeCommands { get; init; }
    public bool ReadOnlyMode { get; init; }
    public bool HasCommandAllowList { get; init; }
    public bool HasCommandBlockList { get; init; }
    public IReadOnlyList<string> SupportedProtocols { get; init; } = ["SSH"];
}

public interface IAgentSessionGateway
{
    AgentGatewayCapabilities Capabilities { get; }
    IReadOnlyList<AgentSessionSnapshot> GetSessions();
    AgentSessionSnapshot? GetSession(Guid sessionId);
    Task<AgentCommandResult> ExecuteCommandAsync(
        AgentCommandRequest request,
        CancellationToken cancellationToken = default);
    Task<AgentFleetInspectionResult> RunReadOnlyDiagnosticAcrossSessionsAsync(
        string scope,
        CancellationToken cancellationToken = default);
    bool TryCancel(Guid requestId);
    bool TryApprove(Guid requestId, out string approvalToken);
    bool TryDeny(Guid requestId);
    IReadOnlyList<AgentAuditEntry> ReadAudit(int limit = AgentAuditLog.MaximumEntries);
}

public interface IAgentSessionHost
{
    IReadOnlyList<IAgentSessionEndpoint> GetAgentSessionEndpoints();
}

public interface IAgentSessionEndpoint
{
    AgentSessionSnapshot Snapshot { get; }
    bool SupportsCommandOutputCapture { get; }
    Task SendCommandAsync(AgentCommandRequest request, CancellationToken cancellationToken);
    Task<AgentCommandExecutionResult> ExecuteCommandAsync(
        AgentCommandRequest request,
        CancellationToken cancellationToken);
}

public sealed record AgentCommandExecutionResult(
    bool RemoteCompletionConfirmed,
    string? Output = null);

public sealed class AgentSessionEndpoint : IAgentSessionEndpoint
{
    private readonly Func<AgentSessionSnapshot> _snapshotProvider;
    private readonly Func<AgentCommandRequest, CancellationToken, Task> _sendCommand;
    private readonly Func<AgentCommandRequest, CancellationToken, Task<string>>? _runCommand;

    public AgentSessionEndpoint(
        Func<AgentSessionSnapshot> snapshotProvider,
        Func<AgentCommandRequest, CancellationToken, Task> sendCommand,
        Func<AgentCommandRequest, CancellationToken, Task<string>>? runCommand = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        _runCommand = runCommand;
    }

    public AgentSessionSnapshot Snapshot => _snapshotProvider();
    public bool SupportsCommandOutputCapture => _runCommand != null;

    public Task SendCommandAsync(AgentCommandRequest request, CancellationToken cancellationToken)
        => _sendCommand(request, cancellationToken);

    public async Task<AgentCommandExecutionResult> ExecuteCommandAsync(
        AgentCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (_runCommand == null)
        {
            await _sendCommand(request, cancellationToken).ConfigureAwait(false);
            return new AgentCommandExecutionResult(false);
        }

        var output = await _runCommand(request, cancellationToken).ConfigureAwait(false);
        return new AgentCommandExecutionResult(true, output);
    }
}

public sealed class DelegateAgentSessionHost : IAgentSessionHost
{
    private readonly Func<IReadOnlyList<IAgentSessionEndpoint>> _endpointProvider;

    public DelegateAgentSessionHost(Func<IReadOnlyList<IAgentSessionEndpoint>> endpointProvider)
    {
        _endpointProvider = endpointProvider ?? throw new ArgumentNullException(nameof(endpointProvider));
    }

    public IReadOnlyList<IAgentSessionEndpoint> GetAgentSessionEndpoints()
        => _endpointProvider();
}

public sealed class AgentCommandDeliveryException : Exception
{
    public AgentCommandDeliveryException(AgentCommandStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public AgentCommandStatus Status { get; }
}
