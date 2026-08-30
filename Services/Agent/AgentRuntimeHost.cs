using System.Text.Json;

namespace CxShell.Services.Agent;

public sealed record AgentRuntimeRequest(
    string RequestId,
    string Method,
    JsonElement Parameters);

public sealed record AgentRuntimeModuleDescriptor(
    string Name,
    IReadOnlyList<string> Methods);

public sealed record AgentRuntimeModuleEvent(
    string Module,
    string RequestId,
    string Method,
    string EventName,
    object? Payload = null);

public sealed record AgentRuntimeEventEnvelope(
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("event")] string EventName,
    [property: System.Text.Json.Serialization.JsonPropertyName("module")] string Module,
    [property: System.Text.Json.Serialization.JsonPropertyName("requestId")] string RequestId,
    [property: System.Text.Json.Serialization.JsonPropertyName("method")] string Method,
    [property: System.Text.Json.Serialization.JsonPropertyName("payload")] JsonElement? Payload = null);

/// <summary>
/// Request-scoped services exposed to a runtime module. The context deliberately
/// contains no UI controls or connection instances so modules remain behind the
/// session gateway boundary.
/// </summary>
public sealed class AgentRuntimeModuleContext
{
    private readonly Action<AgentRuntimeModuleEvent> _emitEvent;

    internal AgentRuntimeModuleContext(
        string moduleName,
        AgentRuntimeRequest request,
        CancellationToken cancellationToken,
        Action<AgentRuntimeModuleEvent> emitEvent)
    {
        ModuleName = moduleName;
        Request = request;
        CancellationToken = cancellationToken;
        _emitEvent = emitEvent;
    }

    public string ModuleName { get; }
    public AgentRuntimeRequest Request { get; }
    public CancellationToken CancellationToken { get; }

    public ValueTask EmitEventAsync(string eventName, object? payload = null)
    {
        CancellationToken.ThrowIfCancellationRequested();
        EmitEvent(eventName, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask EmitEventIgnoringCancellationAsync(string eventName, object? payload = null)
    {
        EmitEvent(eventName, payload);
        return ValueTask.CompletedTask;
    }

    private void EmitEvent(string eventName, object? payload)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("Event name is required.", nameof(eventName));

        _emitEvent(new AgentRuntimeModuleEvent(
            ModuleName,
            Request.RequestId,
            Request.Method,
            eventName.Trim(),
            payload));
    }
}

/// <summary>
/// A self-contained group of CxShell Agent Runtime methods. Modules are hosted
/// in process without exposing CxShell's controls or raw transport services.
/// </summary>
public interface IAgentRuntimeModule
{
    string Name { get; }
    IReadOnlyCollection<string> Methods { get; }

    Task<AgentRuntimeResponse> DispatchAsync(
        AgentRuntimeRequest request,
        AgentRuntimeModuleContext context);
}

/// <summary>
/// Optional long-lived event source for modules whose work continues after a
/// request has returned, such as an accepted Agent run. The Host owns the
/// subscription lifetime and forwards events to Runtime stream consumers.
/// </summary>
public interface IAgentRuntimeEventSource
{
    IDisposable SubscribeRuntimeEvents(Action<AgentRuntimeModuleEvent> observer);
}

public interface IAgentRuntimeHost
{
    IReadOnlyList<AgentRuntimeModuleDescriptor> Modules { get; }
    IReadOnlyList<string> Methods { get; }
    IReadOnlyList<string> ActiveRequestIds { get; }

    void RegisterModule(IAgentRuntimeModule module);

    bool TryCancelRequest(string requestId);

    Task<AgentRuntimeResponse> DispatchAsync(
        AgentRuntimeRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeResponse> DispatchAsync(
        string requestId,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default);

    IDisposable Subscribe(Action<AgentRuntimeModuleEvent> observer);
}

/// <summary>
/// In-process dispatcher for CxShell's Agent Runtime. It owns registered
/// modules and gives each request a bounded, cancellation-aware context.
/// </summary>
public sealed class AgentRuntimeHost : IAgentRuntimeHost, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IAgentRuntimeModule> _methodRoutes = new(StringComparer.Ordinal);
    private readonly List<RegisteredModule> _modules = [];
    private readonly List<IDisposable> _moduleEventSubscriptions = [];
    private readonly List<Action<AgentRuntimeModuleEvent>> _observers = [];
    private readonly Dictionary<string, ActiveRequest> _activeRequests = new(StringComparer.Ordinal);
    private int _disposed;

    public AgentRuntimeHost(IEnumerable<IAgentRuntimeModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        foreach (var module in modules)
            RegisterModule(module);
    }

    public IReadOnlyList<AgentRuntimeModuleDescriptor> Modules
    {
        get
        {
            lock (_gate)
            {
                return _modules
                    .Select(module => new AgentRuntimeModuleDescriptor(module.Name, module.Methods))
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<string> Methods
    {
        get
        {
            lock (_gate)
            {
                return _methodRoutes.Keys
                    .Append(AgentRuntimeMethodNames.RequestCancel)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<string> ActiveRequestIds
    {
        get
        {
            lock (_gate)
                return _activeRequests.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public void RegisterModule(IAgentRuntimeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var name = module.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("Runtime module name is required.", nameof(module));

        var methods = module.Methods?.ToArray() ?? throw new ArgumentException(
            "Runtime module methods are required.",
            nameof(module));
        if (methods.Length == 0)
            throw new ArgumentException("Runtime module must register at least one method.", nameof(module));

        var normalizedMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var normalizedMethod = method?.Trim() ?? string.Empty;
            if (normalizedMethod.Length == 0)
                throw new ArgumentException("Runtime method name is required.", nameof(module));
            if (normalizedMethod.Length > AgentRuntimeContract.MaximumMethodCharacters)
                throw new ArgumentException(
                    $"Runtime method cannot exceed {AgentRuntimeContract.MaximumMethodCharacters} characters.",
                    nameof(module));
            if (string.Equals(normalizedMethod, AgentRuntimeMethodNames.RequestCancel, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Runtime method is reserved by the Host: {AgentRuntimeMethodNames.RequestCancel}");
            if (!normalizedMethods.Add(normalizedMethod))
                throw new InvalidOperationException($"Duplicate runtime method in module: {normalizedMethod}");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_modules.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Duplicate runtime module: {name}");

            foreach (var method in normalizedMethods)
            {
                if (_methodRoutes.ContainsKey(method))
                    throw new InvalidOperationException($"Duplicate runtime method: {method}");
            }

            _modules.Add(new RegisteredModule(name, normalizedMethods.Order(StringComparer.Ordinal).ToArray(), module));
            foreach (var method in normalizedMethods)
                _methodRoutes.Add(method, module);
        }

        if (module is IAgentRuntimeEventSource eventSource)
        {
            var subscription = eventSource.SubscribeRuntimeEvents(Publish);
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    subscription.Dispose();
                }
                else
                {
                    _moduleEventSubscriptions.Add(subscription);
                }
            }
        }
    }

    public bool TryCancelRequest(string requestId)
    {
        var normalizedRequestId = requestId?.Trim() ?? string.Empty;
        if (normalizedRequestId.Length == 0)
            return false;

        ActiveRequest? activeRequest;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !_activeRequests.TryGetValue(normalizedRequestId, out activeRequest))
            {
                return false;
            }
        }

        try
        {
            activeRequest.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public IDisposable Subscribe(Action<AgentRuntimeModuleEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ThrowIfDisposed();
            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    public Task<AgentRuntimeResponse> DispatchAsync(
        string requestId,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
        => DispatchAsync(new AgentRuntimeRequest(requestId, method, parameters), cancellationToken);

    public async Task<AgentRuntimeResponse> DispatchAsync(
        AgentRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var normalizedRequestId = request.RequestId?.Trim() ?? string.Empty;
        var normalizedMethod = request.Method?.Trim() ?? string.Empty;
        if (normalizedRequestId.Length == 0)
            return Error(string.Empty, AgentRuntimeErrorCodes.InvalidRequest, "Request ID is required.");
        if (normalizedRequestId.Length > AgentRuntimeContract.MaximumRequestIdCharacters)
            return Error(
                string.Empty,
                AgentRuntimeErrorCodes.InvalidRequest,
                $"Request ID cannot exceed {AgentRuntimeContract.MaximumRequestIdCharacters} characters.");
        if (normalizedMethod.Length == 0)
            return Error(normalizedRequestId, AgentRuntimeErrorCodes.InvalidRequest, "Method is required.");
        if (normalizedMethod.Length > AgentRuntimeContract.MaximumMethodCharacters)
            return Error(
                normalizedRequestId,
                AgentRuntimeErrorCodes.InvalidRequest,
                $"Method cannot exceed {AgentRuntimeContract.MaximumMethodCharacters} characters.");

        if (string.Equals(normalizedMethod, AgentRuntimeMethodNames.RequestCancel, StringComparison.Ordinal))
            return CancelRequest(normalizedRequestId, request.Parameters);

        IAgentRuntimeModule module;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_methodRoutes.TryGetValue(normalizedMethod, out module!))
                return Error(
                    normalizedRequestId,
                    AgentRuntimeErrorCodes.UnsupportedMethod,
                    $"Unsupported method: {normalizedMethod}");
        }

        var normalizedRequest = request with
        {
            RequestId = normalizedRequestId,
            Method = normalizedMethod
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return Error(
                normalizedRequestId,
                AgentRuntimeErrorCodes.Cancelled,
                "Runtime request was cancelled.");
        }
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRequest = new ActiveRequest(requestCancellation);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_activeRequests.TryAdd(normalizedRequestId, activeRequest))
                return Error(
                    normalizedRequestId,
                    AgentRuntimeErrorCodes.RequestInProgress,
                    $"A runtime request with ID '{normalizedRequestId}' is already in progress.");
        }

        var context = new AgentRuntimeModuleContext(
            module.Name,
            normalizedRequest,
            requestCancellation.Token,
            Publish);

        try
        {
            return await module.DispatchAsync(normalizedRequest, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Error(
                normalizedRequestId,
                AgentRuntimeErrorCodes.Cancelled,
                "Runtime request was cancelled.");
        }
        catch (Exception exception)
        {
            return Error(
                normalizedRequestId,
                AgentRuntimeErrorCodes.Internal,
                TrimException(exception));
        }
        finally
        {
            lock (_gate)
                _activeRequests.Remove(normalizedRequestId);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        IAgentRuntimeModule[] modules;
        ActiveRequest[] activeRequests;
        IDisposable[] moduleEventSubscriptions;
        lock (_gate)
        {
            modules = _modules.Select(item => item.Module).ToArray();
            activeRequests = _activeRequests.Values.ToArray();
            moduleEventSubscriptions = _moduleEventSubscriptions.ToArray();
            _activeRequests.Clear();
            _methodRoutes.Clear();
            _modules.Clear();
            _moduleEventSubscriptions.Clear();
            _observers.Clear();
        }

        foreach (var subscription in moduleEventSubscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch
            {
                // A module event source must not prevent the Host from closing.
            }
        }

        foreach (var activeRequest in activeRequests)
        {
            try
            {
                activeRequest.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        foreach (var module in modules)
        {
            if (module is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private void Publish(AgentRuntimeModuleEvent @event)
    {
        Action<AgentRuntimeModuleEvent>[] observers;
        lock (_gate)
            observers = _observers.ToArray();

        foreach (var observer in observers)
        {
            try
            {
                observer(@event);
            }
            catch
            {
                // A monitoring subscriber must not break the module request.
            }
        }
    }

    private AgentRuntimeResponse CancelRequest(string requestId, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("requestId", out var target) ||
            target.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(target.GetString()))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                "A target requestId is required.");
        }

        var targetRequestId = target.GetString()!.Trim();
        if (targetRequestId.Length > AgentRuntimeContract.MaximumRequestIdCharacters)
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                $"Target requestId cannot exceed {AgentRuntimeContract.MaximumRequestIdCharacters} characters.");
        }

        var cancelled = TryCancelRequest(targetRequestId);
        return new AgentRuntimeResponse
        {
            RequestId = requestId,
            Ok = true,
            Result = JsonSerializer.SerializeToElement(
                new AgentRuntimeRequestCancelResult(
                    cancelled,
                    targetRequestId,
                    cancelled ? null : "The target request was not found or has already completed.")
            )
        };
    }

    private void Unsubscribe(Action<AgentRuntimeModuleEvent> observer)
    {
        lock (_gate)
            _observers.Remove(observer);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AgentRuntimeHost));
    }

    private static AgentRuntimeResponse Error(string requestId, string errorCode, string error)
        => new()
        {
            RequestId = requestId,
            Ok = false,
            ErrorCode = errorCode,
            Error = error
        };

    private static string TrimException(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500] + "...";
    }

    private sealed class Subscription : IDisposable
    {
        private AgentRuntimeHost? _owner;
        private readonly Action<AgentRuntimeModuleEvent> _observer;

        public Subscription(AgentRuntimeHost owner, Action<AgentRuntimeModuleEvent> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(_observer);
        }
    }

    private sealed record RegisteredModule(
        string Name,
        IReadOnlyList<string> Methods,
        IAgentRuntimeModule Module);

    private sealed record ActiveRequest(CancellationTokenSource Cancellation);
}
