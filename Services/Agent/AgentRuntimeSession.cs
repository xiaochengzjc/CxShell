namespace CxShell.Services.Agent;

public enum AgentRuntimeSessionState
{
    NotInitialized,
    Initializing,
    Ready,
    Failed,
    Disposed
}

public sealed record AgentRuntimeSessionStatus(
    AgentRuntimeSessionState State,
    int InitializationAttempt,
    string? RequestId,
    string? ErrorCode,
    string? Error,
    DateTimeOffset ChangedAtUtc);

public interface IAgentRuntimeStatusSource
{
    AgentRuntimeSessionStatus Status { get; }

    event Action<AgentRuntimeSessionStatus>? StatusChanged;
}

/// <summary>
/// Negotiated client boundary for one Agent Runtime connection. It performs a
/// single handshake, caches the contract, and rejects methods that the peer did
/// not advertise before they reach the transport.
/// </summary>
public sealed class AgentRuntimeSession : IAgentRuntimeClient, IAgentRuntimeStatusSource, IDisposable
{
    private readonly object _gate = new();
    private readonly IAgentRuntimeClient _client;
    private Task<AgentRuntimeInitializeResult>? _initializationTask;
    private TaskCompletionSource<AgentRuntimeInitializeResult>? _initializationCompletionSource;
    private AgentRuntimeInitializeResult? _initialization;
    private Exception? _lastInitializationError;
    private AgentRuntimeSessionState _state = AgentRuntimeSessionState.NotInitialized;
    private AgentRuntimeSessionStatus _status = new(
        AgentRuntimeSessionState.NotInitialized,
        0,
        null,
        null,
        null,
        DateTimeOffset.UtcNow);
    private int _initializationAttempt;

    public AgentRuntimeSession(IAgentRuntimeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public AgentRuntimeSessionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public AgentRuntimeInitializeResult? Initialization
    {
        get
        {
            lock (_gate)
                return _initialization;
        }
    }

    public Exception? LastInitializationError
    {
        get
        {
            lock (_gate)
                return _lastInitializationError;
        }
    }

    public AgentRuntimeSessionStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public event Action<AgentRuntimeSessionStatus>? StatusChanged;

    public Task<AgentRuntimeInitializeResult> InitializeAsync(
        string? protocol = null,
        string? protocolVersion = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var expectedProtocol = NormalizeHandshakeValue(
            protocol,
            AgentRuntimeContract.Protocol,
            nameof(protocol));
        var expectedProtocolVersion = NormalizeHandshakeValue(
            protocolVersion,
            AgentRuntimeContract.ProtocolVersion,
            nameof(protocolVersion));

        AgentRuntimeSessionStatus? statusToPublish;
        Task<AgentRuntimeInitializeResult> initializationTask;
        TaskCompletionSource<AgentRuntimeInitializeResult> completionSource;
        string initializationRequestId;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == AgentRuntimeSessionState.Ready &&
                _initialization is { } initialized &&
                string.Equals(initialized.Protocol, expectedProtocol, StringComparison.Ordinal) &&
                string.Equals(initialized.ProtocolVersion, expectedProtocolVersion, StringComparison.Ordinal))
            {
                return Task.FromResult(initialized);
            }

            if (_initializationTask != null)
                return _initializationTask;

            _state = AgentRuntimeSessionState.Initializing;
            _lastInitializationError = null;
            var requestedRequestId = requestId?.Trim();
            initializationRequestId = string.IsNullOrEmpty(requestedRequestId)
                ? $"runtime-init-{Guid.NewGuid():N}"
                : requestedRequestId;

            _initializationAttempt++;
            statusToPublish = UpdateStatusLocked(
                AgentRuntimeSessionState.Initializing,
                initializationRequestId,
                null,
                null);
            completionSource = new TaskCompletionSource<AgentRuntimeInitializeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            initializationTask = completionSource.Task;
            _initializationCompletionSource = completionSource;
            _initializationTask = initializationTask;
        }

        PublishStatus(statusToPublish);
        _ = InitializeCoreAsync(
            expectedProtocol,
            expectedProtocolVersion,
            initializationRequestId,
            cancellationToken,
            completionSource);
        return initializationTask;
    }

    public async Task<AgentRuntimeInfoResult> GetRuntimeInfoAsync(
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        EnsureMethodSupported(AgentRuntimeMethodNames.RuntimeInfo);
        var result = await _client.GetRuntimeInfoAsync(requestId, cancellationToken).ConfigureAwait(false);
        ValidateContract(result.Protocol, result.ProtocolVersion);
        return result;
    }

    public async Task<AgentRuntimeCapabilityResult> CheckCapabilityAsync(
        string capability,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability))
            throw new ArgumentException("A capability is required.", nameof(capability));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        EnsureMethodSupported(AgentRuntimeMethodNames.CapabilitiesCheck);
        return await _client.CheckCapabilityAsync(capability, requestId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRuntimeResponse> SendAsync(
        string method,
        object? parameters = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        EnsureMethodSupported(method);
        return await _client.SendAsync(method, parameters, requestId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T> SendResultAsync<T>(
        string method,
        object? parameters = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        EnsureMethodSupported(method);
        return await _client.SendResultAsync<T>(method, parameters, requestId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRuntimeRequestCancelResult> CancelRequestAsync(
        string requestId,
        string? cancellationRequestId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        EnsureMethodSupported(AgentRuntimeMethodNames.RequestCancel);
        return await _client.CancelRequestAsync(requestId, cancellationRequestId, cancellationToken)
            .ConfigureAwait(false);
    }

    public IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer)
        => _client.SubscribeEvents(observer);

    public void Dispose()
    {
        AgentRuntimeSessionStatus? statusToPublish = null;
        TaskCompletionSource<AgentRuntimeInitializeResult>? completionSource;
        lock (_gate)
        {
            if (_state == AgentRuntimeSessionState.Disposed)
                return;

            _state = AgentRuntimeSessionState.Disposed;
            _initializationTask = null;
            completionSource = _initializationCompletionSource;
            _initializationCompletionSource = null;
            _initialization = null;
            _lastInitializationError = null;
            statusToPublish = UpdateStatusLocked(
                AgentRuntimeSessionState.Disposed,
                _status.RequestId,
                null,
                null);
        }

        PublishStatus(statusToPublish);
        completionSource?.TrySetException(new ObjectDisposedException(nameof(AgentRuntimeSession)));
    }

    private async Task InitializeCoreAsync(
        string protocol,
        string protocolVersion,
        string? requestId,
        CancellationToken cancellationToken,
        TaskCompletionSource<AgentRuntimeInitializeResult> completionSource)
    {
        try
        {
            var result = await _client.InitializeAsync(
                    protocol,
                    protocolVersion,
                    requestId,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateContract(result.Protocol, result.ProtocolVersion);

            AgentRuntimeSessionStatus? statusToPublish;
            lock (_gate)
            {
                ThrowIfDisposed();
                _initialization = result;
                _state = AgentRuntimeSessionState.Ready;
                _initializationTask = null;
                _initializationCompletionSource = null;
                statusToPublish = UpdateStatusLocked(
                    AgentRuntimeSessionState.Ready,
                    _status.RequestId,
                    null,
                    null);
            }

            PublishStatus(statusToPublish);
            completionSource.TrySetResult(result);
        }
        catch (Exception exception)
        {
            AgentRuntimeSessionStatus? statusToPublish = null;
            lock (_gate)
            {
                if (_state != AgentRuntimeSessionState.Disposed)
                {
                    _state = AgentRuntimeSessionState.Failed;
                    _lastInitializationError = exception;
                    _initializationTask = null;
                    _initializationCompletionSource = null;
                    statusToPublish = UpdateStatusLocked(
                        AgentRuntimeSessionState.Failed,
                        _status.RequestId,
                        exception is AgentRuntimeRequestException requestException
                            ? requestException.Response.ErrorCode
                            : null,
                        exception.Message);
                }
            }

            PublishStatus(statusToPublish);
            completionSource.TrySetException(exception);
        }
    }

    private AgentRuntimeSessionStatus UpdateStatusLocked(
        AgentRuntimeSessionState state,
        string? requestId,
        string? errorCode,
        string? error)
    {
        _status = new AgentRuntimeSessionStatus(
            state,
            _initializationAttempt,
            requestId,
            errorCode,
            error,
            DateTimeOffset.UtcNow);
        return _status;
    }

    private void PublishStatus(AgentRuntimeSessionStatus? status)
    {
        if (status == null)
            return;

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // A status observer must never break Runtime request processing.
        }
    }

    private Task<AgentRuntimeInitializeResult> EnsureInitializedAsync(CancellationToken cancellationToken)
        => InitializeAsync(cancellationToken: cancellationToken);

    private void EnsureMethodSupported(string method)
    {
        var normalizedMethod = method?.Trim() ?? string.Empty;
        if (normalizedMethod.Length == 0)
            throw new ArgumentException("Runtime method is required.", nameof(method));

        AgentRuntimeInitializeResult? initialized;
        lock (_gate)
        {
            ThrowIfDisposed();
            initialized = _initialization;
        }

        if (initialized == null || !initialized.Methods.Contains(normalizedMethod, StringComparer.Ordinal))
        {
            throw new AgentRuntimeMethodNotSupportedException(
                normalizedMethod,
                initialized?.Methods ?? []);
        }
    }

    private void ValidateContract(string protocol, string protocolVersion)
    {
        if (!string.Equals(protocol, AgentRuntimeContract.Protocol, StringComparison.Ordinal) ||
            !string.Equals(protocolVersion, AgentRuntimeContract.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new AgentRuntimeProtocolException(
                $"Runtime contract '{protocol}/{protocolVersion}' does not match " +
                $"'{AgentRuntimeContract.Protocol}/{AgentRuntimeContract.ProtocolVersion}'.");
        }
    }

    private static string NormalizeHandshakeValue(string? value, string defaultValue, string parameterName)
    {
        if (value == null)
            return defaultValue;

        var normalized = value.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("The handshake value cannot be empty.", parameterName);

        return normalized;
    }

    private void ThrowIfDisposed()
    {
        if (_state == AgentRuntimeSessionState.Disposed)
            throw new ObjectDisposedException(nameof(AgentRuntimeSession));
    }
}

public sealed class AgentRuntimeMethodNotSupportedException : AgentRuntimeProtocolException
{
    public AgentRuntimeMethodNotSupportedException(
        string method,
        IReadOnlyCollection<string> availableMethods)
        : base($"Runtime method '{method}' was not advertised by the negotiated contract.")
    {
        Method = method;
        AvailableMethods = availableMethods;
    }

    public string Method { get; }
    public IReadOnlyCollection<string> AvailableMethods { get; }
}
