using System.Collections.Concurrent;
using System.Security.Cryptography;
using CxShell.Models;

namespace CxShell.Services.Agent;

public sealed class AgentSessionGateway : IAgentSessionGateway, IDisposable
{
    public const int MaximumCommandLength = 64 * 1024;
    public static readonly TimeSpan DefaultCommandTimeout = AgentCommandTimeoutPolicy.DefaultTimeout;
    public static readonly TimeSpan MaximumCommandTimeout = AgentCommandTimeoutPolicy.MaximumTimeout;
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CompletedRequestLifetime = TimeSpan.FromMinutes(10);
    public const int MaximumCapturedOutputLength = 512 * 1024;
    private const int MaximumPendingApprovals = 64;
    private const int MaximumCompletedRequests = 256;

    private readonly IAgentSessionHost _host;
    private readonly AgentPermissionPolicy _permissionPolicy;
    private readonly AgentAuditLog _auditLog;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRequests = new();
    private readonly ConcurrentDictionary<Guid, CompletedRequest> _completedRequests = new();
    private readonly ConcurrentDictionary<Guid, PendingApproval> _pendingApprovals = new();
    private int _disposed;

    public AgentSessionGateway(
        IAgentSessionHost host,
        AgentPermissionPolicy? permissionPolicy = null,
        AgentAuditLog? auditLog = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _permissionPolicy = permissionPolicy ?? new AgentPermissionPolicy();
        _auditLog = auditLog ?? new AgentAuditLog();
    }

    public AgentGatewayCapabilities Capabilities
        => new()
        {
            SupportsCommandOutputCapture = GetEndpoints().Any(endpoint => endpoint.SupportsCommandOutputCapture),
            SupportsReadOnlyDiagnostics = GetEndpoints().Any(endpoint => endpoint.SupportsCommandOutputCapture),
            AllowsCommandExecution = _permissionPolicy.AllowCommandExecution,
            PermissionMode = AgentPermissionPolicy.NormalizePermissionMode(_permissionPolicy.PermissionMode),
            RequiresApprovalForDangerousCommands = _permissionPolicy.RequireApprovalForDangerousCommands,
            RequiresApprovalForChangeCommands = _permissionPolicy.RequireApprovalForChangeCommands
            ,ReadOnlyMode = _permissionPolicy.ReadOnlyMode
            ,HasCommandAllowList = !string.IsNullOrWhiteSpace(_permissionPolicy.AllowedCommandPrefixes)
            ,HasCommandBlockList = !string.IsNullOrWhiteSpace(_permissionPolicy.BlockedCommandPrefixes)
        };

    public IReadOnlyList<AgentSessionSnapshot> GetSessions()
    {
        ThrowIfDisposed();
        return GetEndpoints()
            .Select(endpoint => endpoint.Snapshot)
            .Where(snapshot => snapshot.Protocol == SessionProtocol.SSH)
            .ToList();
    }

    public AgentSessionSnapshot? GetSession(Guid sessionId)
    {
        ThrowIfDisposed();
        if (sessionId == Guid.Empty)
            return null;

        return GetSessions().FirstOrDefault(session => session.SessionId == sessionId);
    }

    public async Task<AgentCommandResult> ExecuteCommandAsync(
        AgentCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var startedAt = DateTimeOffset.UtcNow;
        if (request.RequestId == Guid.Empty || request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Command))
            return Complete(request, AgentCommandStatus.InvalidRequest, "Request ID, session ID and command are required.", startedAt);

        if (TryGetCompletedRequest(request, startedAt, out var cachedResult))
            return cachedResult;

        var endpoint = FindEndpoint(request.SessionId);

        if (request.Command.Length > MaximumCommandLength)
            return Complete(request, AgentCommandStatus.InvalidRequest, $"Command length cannot exceed {MaximumCommandLength} characters.", startedAt);

        if (!IsValidTimeout(request.Timeout))
            return Complete(request, AgentCommandStatus.InvalidRequest, $"Timeout must be between 100 ms and {MaximumCommandTimeout.TotalMinutes:0} minutes.", startedAt);

        var effectiveTimeout = AgentCommandTimeoutPolicy.Resolve(
            request.Command,
            request.Timeout,
            hasExplicitTimeout: true);
        if (!IsValidTimeout(effectiveTimeout))
            return Complete(request, AgentCommandStatus.InvalidRequest, $"Timeout must be between 100 ms and {MaximumCommandTimeout.TotalMinutes:0} minutes.", startedAt);

        if (endpoint == null)
            return Complete(request, AgentCommandStatus.SessionNotFound, "The requested session is not open.", startedAt);

        var snapshot = endpoint.Snapshot;
        if (!snapshot.IsConnected)
            return Complete(request, AgentCommandStatus.SessionNotConnected, "The requested session is not connected.", startedAt);

        if (snapshot.Protocol != SessionProtocol.SSH)
            return Complete(request, AgentCommandStatus.UnsupportedProtocol, "Only SSH terminal sessions are supported.", startedAt);

        var hasApprovalToken = !string.IsNullOrWhiteSpace(request.ApprovalToken);
        var approvalConsumed = hasApprovalToken &&
            TryConsumeApproval(request, request.ApprovalToken!);
        if (hasApprovalToken && !approvalConsumed)
        {
            return Complete(
                request,
                AgentCommandStatus.Denied,
                "The approval is invalid or has expired.",
                startedAt);
        }

        var permissionCommand = request.ApprovalGranted &&
                                !string.IsNullOrWhiteSpace(request.ApprovedCommand)
            ? request.ApprovedCommand
            : request.Command;
        var permission = _permissionPolicy.Evaluate(snapshot, permissionCommand);
        var approvalGranted = request.ApprovalGranted || approvalConsumed;
        if (!permission.IsAllowed &&
            !(approvalGranted && permission.ApprovalRequired))
        {
            var approvalRequired = permission.ApprovalRequired;
            if (approvalRequired)
                RegisterApproval(request);

            return Complete(
                request,
                AgentCommandStatus.Denied,
                permission.Reason,
                startedAt,
                approvalRequired: approvalRequired,
                permission: permission);
        }

        using var timeoutCancellation = new CancellationTokenSource(effectiveTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        if (!_activeRequests.TryAdd(request.RequestId, linkedCancellation))
            return Complete(request, AgentCommandStatus.InvalidRequest, "The request ID is already active.", startedAt);

        AgentCommandResult result;
        try
        {
            var requestToExecute = effectiveTimeout == request.Timeout
                ? request
                : request with { Timeout = effectiveTimeout };
            var execution = await endpoint.ExecuteCommandAsync(
                    requestToExecute,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            result = Complete(
                request,
                AgentCommandStatus.Sent,
                execution.RemoteCompletionConfirmed
                    ? "Remote command completed successfully."
                    : "Command sent to the terminal input queue.",
                startedAt,
                execution,
                permission: permission,
                approvalGranted: approvalGranted);
        }
        catch (AgentCommandDeliveryException ex)
        {
            result = Complete(request, ex.Status, ex.Message, startedAt, permission: permission);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            result = Complete(
                request,
                AgentCommandStatus.TimedOut,
                "Command dispatch timed out.",
                startedAt,
                permission: permission);
        }
        catch (OperationCanceledException)
        {
            result = Complete(
                request,
                AgentCommandStatus.Cancelled,
                "Command dispatch was cancelled.",
                startedAt,
                permission: permission);
        }
        catch (Exception ex)
        {
            result = Complete(
                request,
                AgentCommandStatus.Failed,
                TrimException(ex),
                startedAt,
                permission: permission);
        }
        finally
        {
            _activeRequests.TryRemove(request.RequestId, out _);
        }

        StoreCompletedRequest(request, result);
        return result;
    }

    public async Task<AgentFleetInspectionResult> RunReadOnlyDiagnosticAcrossSessionsAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedScope = scope?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AgentDiagnosticCatalog.Scopes.Contains(normalizedScope, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"scope must be one of: {string.Join(", ", AgentDiagnosticCatalog.Scopes)}.",
                nameof(scope));
        }

        var endpoints = GetEndpoints()
            .Where(endpoint => endpoint.Snapshot.Protocol == SessionProtocol.SSH &&
                               endpoint.Snapshot.IsConnected)
            .ToList();
        var results = new ConcurrentBag<AgentFleetInspectionItem>();
        using var concurrency = new SemaphoreSlim(4, 4);

        var tasks = endpoints.Select(endpoint => InspectEndpointAsync(
            endpoint,
            normalizedScope,
            results,
            concurrency,
            cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var orderedResults = results
            .OrderBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.SessionId)
            .ToList();
        var successCount = orderedResults.Count(result =>
            string.Equals(result.Status, AgentCommandStatus.Sent.ToString(), StringComparison.Ordinal));

        return new AgentFleetInspectionResult(
            normalizedScope,
            orderedResults.Count,
            successCount,
            orderedResults.Count - successCount,
            orderedResults);
    }

    public bool TryCancel(Guid requestId)
    {
        ThrowIfDisposed();
        return requestId != Guid.Empty &&
               _activeRequests.TryGetValue(requestId, out var cancellation) &&
               TryCancel(cancellation);
    }

    public bool TryApprove(Guid requestId, out string approvalToken)
    {
        ThrowIfDisposed();
        approvalToken = string.Empty;
        if (requestId == Guid.Empty ||
            !_pendingApprovals.TryGetValue(requestId, out var approval))
        {
            return false;
        }

        if (approval.IsExpired(DateTimeOffset.UtcNow))
        {
            _pendingApprovals.TryRemove(requestId, out _);
            return false;
        }

        approvalToken = approval.Approve();
        return true;
    }

    public bool TryDeny(Guid requestId)
    {
        ThrowIfDisposed();
        return requestId != Guid.Empty && _pendingApprovals.TryRemove(requestId, out _);
    }

    public IReadOnlyList<AgentAuditEntry> ReadAudit(int limit = AgentAuditLog.MaximumEntries)
    {
        ThrowIfDisposed();
        return _auditLog.ReadRecent(limit);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var cancellation in _activeRequests.Values)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _activeRequests.Clear();
        _completedRequests.Clear();
        _pendingApprovals.Clear();
    }

    private IReadOnlyList<IAgentSessionEndpoint> GetEndpoints()
        => _host.GetAgentSessionEndpoints() ?? [];

    private async Task InspectEndpointAsync(
        IAgentSessionEndpoint endpoint,
        string scope,
        ConcurrentBag<AgentFleetInspectionItem> results,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = endpoint.Snapshot;
            if (!AgentDiagnosticCatalog.TryCreatePlan(snapshot, scope, out var plan, out var planError))
            {
                results.Add(new AgentFleetInspectionItem(
                    snapshot.SessionId,
                    snapshot.Name,
                    snapshot.Host,
                    snapshot.Platform,
                    AgentCommandStatus.InvalidRequest.ToString(),
                    planError ?? "The diagnostic plan could not be created.",
                    false));
                return;
            }

            if (!endpoint.SupportsCommandOutputCapture)
            {
                results.Add(new AgentFleetInspectionItem(
                    snapshot.SessionId,
                    snapshot.Name,
                    snapshot.Host,
                    snapshot.Platform,
                    AgentCommandStatus.Failed.ToString(),
                    "The SSH session does not support captured command output.",
                    false));
                return;
            }

            var commandResult = await ExecuteCommandAsync(
                new AgentCommandRequest
                {
                    RequestId = Guid.NewGuid(),
                    SessionId = snapshot.SessionId,
                    Command = plan.Command,
                    DisplayCommand = $"fleet {plan.DisplayCommand}",
                    Timeout = plan.Timeout,
                    AppendLineEnding = true
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new AgentFleetInspectionItem(
                snapshot.SessionId,
                snapshot.Name,
                snapshot.Host,
                snapshot.Platform,
                commandResult.Status.ToString(),
                commandResult.Message,
                commandResult.RemoteCompletionConfirmed,
                commandResult.Output));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var snapshot = endpoint.Snapshot;
            results.Add(new AgentFleetInspectionItem(
                snapshot.SessionId,
                snapshot.Name,
                snapshot.Host,
                snapshot.Platform,
                AgentCommandStatus.Failed.ToString(),
                TrimException(ex),
                false));
        }
        finally
        {
            concurrency.Release();
        }
    }

    private IAgentSessionEndpoint? FindEndpoint(Guid sessionId)
        => sessionId == Guid.Empty
            ? null
            : GetEndpoints().FirstOrDefault(endpoint => endpoint.Snapshot.SessionId == sessionId);

    private AgentCommandResult Complete(
        AgentCommandRequest request,
        AgentCommandStatus status,
        string message,
        DateTimeOffset startedAt,
        AgentCommandExecutionResult? execution = null,
        bool approvalRequired = false,
        AgentPermissionResult? permission = null,
        bool approvalGranted = false)
    {
        var executionState = status switch
        {
            AgentCommandStatus.Sent when execution?.RemoteCompletionConfirmed == true
                => AgentCommandExecutionState.Completed,
            AgentCommandStatus.Sent => AgentCommandExecutionState.Dispatched,
            AgentCommandStatus.Denied => AgentCommandExecutionState.Denied,
            AgentCommandStatus.Cancelled => AgentCommandExecutionState.Cancelled,
            AgentCommandStatus.TimedOut => AgentCommandExecutionState.Unknown,
            AgentCommandStatus.Failed => AgentCommandExecutionState.Unknown,
            _ => AgentCommandExecutionState.Failed
        };
        var result = new AgentCommandResult
        {
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            Status = status,
            ExecutionState = executionState,
            Risk = permission?.Risk ?? AgentCommandRisk.ReadOnly,
            Message = message,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            RemoteCompletionConfirmed = execution?.RemoteCompletionConfirmed == true,
            ApprovalRequired = approvalRequired,
            Output = LimitCapturedOutput(execution?.Output)
        };
        _auditLog.Record(request, result, permission: permission, approvalGranted: approvalGranted);
        return result;
    }

    private bool TryGetCompletedRequest(
        AgentCommandRequest request,
        DateTimeOffset now,
        out AgentCommandResult result)
    {
        result = default!;
        CleanupCompletedRequests(now);
        if (!_completedRequests.TryGetValue(request.RequestId, out var completed))
            return false;

        if (completed.SessionId != request.SessionId ||
            !string.Equals(
                completed.CommandFingerprint,
                AgentAuditLog.Fingerprint(request.Command),
                StringComparison.Ordinal))
        {
            result = Complete(
                request,
                AgentCommandStatus.InvalidRequest,
                "The request ID was already used for a different command.",
                now);
            return true;
        }

        result = completed.Result;
        return true;
    }

    private void StoreCompletedRequest(AgentCommandRequest request, AgentCommandResult result)
    {
        if (request.RequestId == Guid.Empty || result.Status == AgentCommandStatus.Denied)
            return;

        CleanupCompletedRequests(DateTimeOffset.UtcNow);
        _completedRequests[request.RequestId] = new CompletedRequest(
            request.SessionId,
            AgentAuditLog.Fingerprint(request.Command),
            DateTimeOffset.UtcNow,
            result);

        if (_completedRequests.Count <= MaximumCompletedRequests)
            return;

        foreach (var entry in _completedRequests
                     .OrderBy(pair => pair.Value.CompletedAtUtc)
                     .Take(_completedRequests.Count - MaximumCompletedRequests))
        {
            _completedRequests.TryRemove(entry.Key, out _);
        }
    }

    private void CleanupCompletedRequests(DateTimeOffset now)
    {
        foreach (var entry in _completedRequests)
        {
            if (now - entry.Value.CompletedAtUtc > CompletedRequestLifetime)
                _completedRequests.TryRemove(entry.Key, out _);
        }
    }

    private static bool IsValidTimeout(TimeSpan timeout)
        => timeout >= TimeSpan.FromMilliseconds(100) && timeout <= MaximumCommandTimeout;

    private static string? LimitCapturedOutput(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= MaximumCapturedOutputLength)
            return output;

        return output[..MaximumCapturedOutputLength] + "\n[output truncated by CxShell]";
    }

    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static string TrimException(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500] + "...";
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AgentSessionGateway));
    }

    private void RegisterApproval(AgentCommandRequest request)
    {
        if (_pendingApprovals.Count >= MaximumPendingApprovals)
            return;

        var approval = new PendingApproval(
            request.RequestId,
            request.SessionId,
            AgentAuditLog.Fingerprint(request.Command),
            DateTimeOffset.UtcNow);
        _pendingApprovals.TryAdd(request.RequestId, approval);
    }

    private bool TryConsumeApproval(AgentCommandRequest request, string token)
    {
        if (!_pendingApprovals.TryGetValue(request.RequestId, out var approval))
            return false;

        if (approval.IsExpired(DateTimeOffset.UtcNow) ||
            !approval.TryConsume(request.SessionId, AgentAuditLog.Fingerprint(request.Command), token))
        {
            _pendingApprovals.TryRemove(request.RequestId, out _);
            return false;
        }

        _pendingApprovals.TryRemove(request.RequestId, out _);
        return true;
    }

    private sealed class PendingApproval
    {
        private readonly object _gate = new();
        private string? _token;
        private bool _consumed;

        public PendingApproval(
            Guid requestId,
            Guid sessionId,
            string commandFingerprint,
            DateTimeOffset createdAtUtc)
        {
            RequestId = requestId;
            SessionId = sessionId;
            CommandFingerprint = commandFingerprint;
            CreatedAtUtc = createdAtUtc;
        }

        public Guid RequestId { get; }
        public Guid SessionId { get; }
        public string CommandFingerprint { get; }
        public DateTimeOffset CreatedAtUtc { get; }

        public bool IsExpired(DateTimeOffset now)
            => now - CreatedAtUtc > ApprovalLifetime;

        public string Approve()
        {
            lock (_gate)
            {
                _token ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                return _token;
            }
        }

        public bool TryConsume(Guid sessionId, string commandFingerprint, string token)
        {
            lock (_gate)
            {
                if (_consumed ||
                    string.IsNullOrWhiteSpace(_token) ||
                    SessionId != sessionId ||
                    !string.Equals(CommandFingerprint, commandFingerprint, StringComparison.Ordinal))
                    return false;

                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(_token),
                        System.Text.Encoding.UTF8.GetBytes(token)))
                {
                    return false;
                }

                _consumed = true;
                return true;
            }
        }
    }

    private sealed record CompletedRequest(
        Guid SessionId,
        string CommandFingerprint,
        DateTimeOffset CompletedAtUtc,
        AgentCommandResult Result);
}
