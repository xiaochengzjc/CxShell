using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using CxShell.Models;
using CxShell.Services.Agent.OpenCoworkRuntime;

namespace CxShell.Services.Agent;

/// <summary>
/// Owns the asynchronous lifecycle of an Agent run. The coordinator can call
/// only the model client and the session gateway; it never receives a control,
/// terminal widget, or raw SSH connection.
/// </summary>
public sealed class AgentRunCoordinator : IAgentRunCoordinator, IDisposable
{
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumRunTimeout = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RecoveryLifetime = TimeSpan.FromDays(7);
    public const int MaximumRunIdLength = 128;
    // Zero means the loop is governed by the independent request/tool/time limits.
    public const int MaximumIterations = 0;
    public const int MaximumModelRequestsPerRun = 32;
    public const int MaximumToolCallsPerRun = 64;
    public const int MaximumModelRetryAttempts = 3;
    public const int MaximumCredentialAttempts = 3;
    public const int MaximumToolResultCharacters = 96 * 1024;
    public const int MaximumToolCallIdCharacters = 256;
    public const int MaximumToolNameCharacters = 128;
    public const int MaximumToolArgumentsCharacters = 64 * 1024;
    public const int MaximumAppendedMessagesPerRun = 32;
    public const int MaximumRetainedRuns = 32;
    public const int MaximumEventsPerRun = 128;
    public const int DefaultRunListLimit = 32;
    public const int DefaultEventReadLimit = 100;
    public const int MaximumEventReadLimit = 256;
    public static readonly TimeSpan StreamTextDeltaBatchInterval = TimeSpan.FromMilliseconds(50);
    public const int MaximumStreamTextDeltaBatchCharacters = 4 * 1024;
    public const string SessionCommandToolName = "session_command";
    public const string SessionInfoToolName = "session_info";
    public const string DiagnosticRunToolName = "diagnostic_run";
    public const string RunbookRunToolName = "runbook_run";
    public const string FleetDiagnosticToolName = "fleet_diagnostic";
    public static readonly TimeSpan CredentialRequestLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumCredentialCharacters = 4096;

    private static readonly AgentToolDefinition SessionCommandTool = new(
        SessionCommandToolName,
        "Send one safe shell command to the SSH session selected for this run. " +
        "The command is still checked by CxShell's permission policy.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    description = "The shell command to send to the remote SSH terminal."
                },
                timeoutMs = new
                {
                    type = "integer",
                    minimum = 100,
                    maximum = (int)AgentSessionGateway.MaximumCommandTimeout.TotalMilliseconds,
                    description = "Maximum time to wait while dispatching the command."
                }
            },
            required = new[] { "command" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition SessionInfoTool = new(
        SessionInfoToolName,
        "Read connection metadata for the selected SSH session. This does not run a remote command and never returns secrets.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition DiagnosticRunTool = new(
        DiagnosticRunToolName,
        "Run one fixed, read-only CxShell diagnostic on the selected SSH session. " +
        "Use this for system, disk, network, services, processes, or all. Do not use it to change the server.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                scope = new
                {
                    type = "string",
                    @enum = AgentDiagnosticCatalog.Scopes,
                    description = "The read-only diagnostic scope to collect."
                }
            },
            required = new[] { "scope" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition RunbookRunTool = new(
        RunbookRunToolName,
        "Run one fixed, read-only CxShell troubleshooting workflow. Use ssh for SSH service and port checks, " +
        "rdp for Windows Remote Desktop checks, or health for a complete host overview.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                scope = new
                {
                    type = "string",
                    @enum = AgentDiagnosticRunbookCatalog.Runbooks,
                    description = "The fixed read-only troubleshooting workflow to run."
                }
            },
            required = new[] { "scope" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition FleetDiagnosticTool = new(
        FleetDiagnosticToolName,
        "Inspect all currently connected SSH sessions with one fixed, read-only diagnostic. " +
        "Use this for a multi-server disk, network, services, processes, system, or health check. " +
        "It never connects to saved sessions automatically.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                scope = new
                {
                    type = "string",
                    @enum = AgentDiagnosticCatalog.Scopes,
                    description = "The fixed read-only diagnostic scope for every currently connected SSH session."
                }
            },
            required = new[] { "scope" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition LogsTool = new(
        AgentReadOnlyToolCatalog.LogsToolName,
        "Read a bounded tail of a known system, application, or security log. This is read-only and the log source is validated by CxShell.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                source = new
                {
                    type = "string",
                    @enum = AgentReadOnlyToolCatalog.LogSources,
                    description = "Known log source to inspect."
                },
                lines = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 200,
                    description = "Maximum number of log lines."
                }
            },
            required = new[] { "source" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition PortCheckTool = new(
        AgentReadOnlyToolCatalog.PortCheckToolName,
        "Check whether one TCP port is listening on the selected SSH host. This never changes remote state.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                port = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 65535,
                    description = "TCP port number."
                }
            },
            required = new[] { "port" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition ServiceDetailTool = new(
        AgentReadOnlyToolCatalog.ServiceDetailToolName,
        "Read details for one service using a validated service name. This is read-only.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                service = new
                {
                    type = "string",
                    minLength = 1,
                    maxLength = 64,
                    description = "Service name; letters, numbers, '.', '@', ':', '_' and '-' are accepted."
                }
            },
            required = new[] { "service" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition FilePreviewTool = new(
        AgentReadOnlyToolCatalog.FilePreviewToolName,
        "Preview a bounded portion of one known configuration or log file. Arbitrary paths are not accepted.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                target = new
                {
                    type = "string",
                    @enum = AgentReadOnlyToolCatalog.FileTargets,
                    description = "Known file target."
                },
                lines = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 200,
                    description = "Maximum number of lines."
                }
            },
            required = new[] { "target" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition PackageQueryTool = new(
        AgentReadOnlyToolCatalog.PackageQueryToolName,
        "Look up one installed package or executable without changing the selected SSH host. " +
        "The package name is validated and the platform-specific query is supplied by CxShell.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new
                {
                    type = "string",
                    minLength = 1,
                    maxLength = 100,
                    description = "Package or executable name to query."
                }
            },
            required = new[] { "name" },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition RuntimeCheckTool = new(
        AgentReadOnlyToolCatalog.RuntimeCheckToolName,
        "Check installed Java, Python, .NET, Node.js, or PowerShell runtime versions. " +
        "Use all to check every supported runtime; this is read-only.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                runtime = new
                {
                    type = "string",
                    @enum = AgentReadOnlyToolCatalog.RuntimeNames,
                    @default = "all",
                    description = "Runtime to check, or all for the complete supported list."
                }
            },
            additionalProperties = false
        }));

    private static readonly AgentToolDefinition DiskCleanupAdviceTool = new(
        AgentReadOnlyToolCatalog.DiskCleanupAdviceToolName,
        "Analyze disk usage and likely log or temporary-file cleanup candidates without deleting anything. " +
        "Use summary, logs, temp, or all.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                scope = new
                {
                    type = "string",
                    @enum = AgentReadOnlyToolCatalog.CleanupScopes,
                    @default = "all",
                    description = "Read-only analysis area."
                }
            },
            additionalProperties = false
        }));

    private readonly IAgentSessionGateway _gateway;
    private readonly Func<AgentProviderSettings?> _providerSettings;
    private readonly IAgentModelClient _modelClient;
    private readonly IAgentRunHistoryStore _historyStore;
    private readonly OpenCoworkRuntimeLoop _runtimeLoop;
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _activeSessionRuns = new();
    private readonly ConcurrentDictionary<string, RunEventHistory> _runHistories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentRunRecoveryState> _recoverableRuns = new(StringComparer.Ordinal);
    private readonly object _observersGate = new();
    private readonly List<Action<AgentRuntimeStreamEnvelope>> _observers = [];
    private long _generatedRunId;
    private int _disposed;

    public AgentRunCoordinator(
        IAgentSessionGateway gateway,
        Func<AgentProviderSettings?>? providerSettings = null,
        IAgentModelClient? modelClient = null,
        IAgentRunHistoryStore? historyStore = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _providerSettings = providerSettings ?? (() => null);
        _modelClient = modelClient ?? new OpenAiCompatibleAgentModelClient();
        _historyStore = historyStore ?? new NullAgentRunHistoryStore();
        _runtimeLoop = new OpenCoworkRuntimeLoop(new OpenCoworkRuntimeLoopOptions
        {
            MaximumIterations = MaximumIterations,
            MaximumModelRequests = MaximumModelRequestsPerRun,
            MaximumToolCalls = MaximumToolCallsPerRun
        });
        foreach (var snapshot in _historyStore.Load()
                     .OrderByDescending(run => run.StartedAtUtc)
                     .Take(MaximumRetainedRuns))
        {
            _runHistories[snapshot.RunId] = RunEventHistory.FromSnapshot(snapshot);
        }

        foreach (var recovery in _historyStore.LoadRecoverable()
                     .OrderByDescending(run => run.Snapshot.StartedAtUtc)
                     .Take(MaximumRetainedRuns))
        {
            if (!Guid.TryParse(recovery.Snapshot.SessionId, out var recoverySessionId) ||
                recoverySessionId == Guid.Empty ||
                recovery.Messages is not { Count: > 0 })
            {
                continue;
            }

            if (_runHistories.TryGetValue(recovery.Snapshot.RunId, out var existing))
            {
                // A completed snapshot wins over a stale recovery entry that
                // could remain after a process stopped between two writes.
                if (!existing.ToSnapshot().CanResume)
                    continue;

                _recoverableRuns[recovery.Snapshot.RunId] = recovery;
                continue;
            }

            var interruptedSnapshot = recovery.Snapshot with
            {
                Status = "interrupted",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                EndReason = "application_restart",
                CanResume = true
            };
            _runHistories[recovery.Snapshot.RunId] = RunEventHistory.FromSnapshot(interruptedSnapshot);
            _recoverableRuns[recovery.Snapshot.RunId] = recovery with { Snapshot = interruptedSnapshot };
        }
        PersistRunState();
    }

    public static IReadOnlyList<AgentToolDefinition> GetToolDefinitions()
        =>
        [
            SessionCommandTool,
            SessionInfoTool,
            DiagnosticRunTool,
            RunbookRunTool,
            FleetDiagnosticTool,
            LogsTool,
            PortCheckTool,
            ServiceDetailTool,
            FilePreviewTool,
            PackageQueryTool,
            RuntimeCheckTool,
            DiskCleanupAdviceTool
        ];

    public AgentRunStartResult Start(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var requestedRunId = request.RunId?.Trim();
        if (requestedRunId?.Length > MaximumRunIdLength)
        {
            return new(
                false,
                string.Empty,
                $"runId cannot exceed {MaximumRunIdLength} characters.");
        }

        var runId = NormalizeRunId(request.RunId);
        if (request.SessionId == Guid.Empty)
            return new(false, runId, "A valid SSH sessionId is required.");

        if (request.Messages == null || request.Messages.Count == 0)
            return new(false, runId, "At least one chat message is required.");

        if (!IsValidTimeout(request.Timeout))
        {
            return new(
                false,
                runId,
                $"Run timeout must be between 100 ms and {MaximumRunTimeout.TotalMinutes:0} minutes.");
        }

        var session = _gateway.GetSession(request.SessionId);
        if (session == null)
            return new(false, runId, "The requested session is not open or is not an SSH session.");
        if (!session.IsConnected)
            return new(false, runId, "The requested session is not connected.");
        if (session.Protocol != SessionProtocol.SSH)
            return new(false, runId, "Only SSH terminal sessions are supported.");

        var provider = _providerSettings();
        var validation = AgentProviderConfiguration.Validate(provider);
        if (!validation.IsValid || provider == null)
            return new(false, runId, validation.Message);

        var activeRun = new ActiveRun(
            runId,
            request.SessionId,
            provider.BuiltinId,
            string.IsNullOrWhiteSpace(request.Model) ? provider.Model : request.Model,
            BuildPromptPreview(request.Messages));
        if (!_activeRuns.TryAdd(runId, activeRun))
            return new(false, runId, $"Agent run already exists: {runId}");
        if (!_activeSessionRuns.TryAdd(request.SessionId, runId))
        {
            _activeRuns.TryRemove(runId, out _);
            return new(false, runId, "Another Agent run is already active for this SSH session.");
        }

        _runHistories[runId] = activeRun.EventHistory;
        _recoverableRuns[runId] = CreateRecoveryState(activeRun, request);
        PruneRunHistories();
        PersistRunState();
        _ = ExecuteAsync(activeRun, request, provider);
        return new(true, runId);
    }

    public AgentRunCancellationResult Cancel(string runId)
    {
        ThrowIfDisposed();
        var normalized = runId?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return new(false, string.Empty, "A runId is required.");

        if (!_activeRuns.TryGetValue(normalized, out var activeRun))
            return new(false, normalized, "The agent run was not found or has already completed.");

        try
        {
            activeRun.Cancellation.Cancel();
            return new(true, normalized);
        }
        catch (ObjectDisposedException)
        {
            return new(false, normalized, "The agent run has already completed.");
        }
    }

    public AgentRunAppendMessagesResult AppendMessages(
        string runId,
        IReadOnlyList<AgentChatMessage> messages)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0)
            return new(false, string.Empty, 0, "A runId is required.");
        if (messages == null || messages.Count == 0)
            return new(false, normalizedRunId, 0, "At least one follow-up message is required.");
        if (messages.Count > MaximumAppendedMessagesPerRun)
        {
            return new(
                false,
                normalizedRunId,
                0,
                $"A single append cannot contain more than {MaximumAppendedMessagesPerRun} messages.");
        }

        foreach (var message in messages)
        {
            if (message == null ||
                !string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                !HasMessageContent(message) ||
                message.Content.Length > AgentRuntimeContract.MaximumMessageCharacters)
            {
                return new(
                    false,
                    normalizedRunId,
                    0,
                    "Follow-up messages must be non-empty user messages within the size limit.");
            }
        }

        if (!_activeRuns.TryGetValue(normalizedRunId, out var activeRun))
            return new(false, normalizedRunId, 0, "The agent run was not found or has already completed.");
        if (!activeRun.TryQueueMessages(messages, out var error))
            return new(false, normalizedRunId, 0, error);

        if (_recoverableRuns.TryGetValue(normalizedRunId, out var recovery) &&
            recovery.Messages.Count + messages.Count <= AgentRuntimeContract.MaximumMessageCount)
        {
            _recoverableRuns[normalizedRunId] = recovery with
            {
                Messages = recovery.Messages.Concat(messages).ToArray()
            };
            PersistRecoverableRuns();
        }

        return new(true, normalizedRunId, messages.Count);
    }

    public AgentRunStopResult RequestStop(string runId)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0)
            return new(false, string.Empty, "A runId is required.");
        if (!_activeRuns.TryGetValue(normalizedRunId, out var activeRun))
        {
            return new(
                false,
                normalizedRunId,
                "The agent run was not found or has already completed.");
        }

        activeRun.RequestStop();
        return new(true, normalizedRunId);
    }

    public AgentRunResumeResult Resume(string runId)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0)
            return new(false, string.Empty, string.Empty, Guid.Empty, "A runId is required.");

        if (!_recoverableRuns.TryGetValue(normalizedRunId, out var recovery))
        {
            return new(
                false,
                normalizedRunId,
                string.Empty,
                Guid.Empty,
                "The agent run is not available for continuation.");
        }

        var sessionId = Guid.Parse(recovery.Snapshot.SessionId);
        var timeout = recovery.TimeoutMs >= 100 &&
                      recovery.TimeoutMs <= (int)MaximumRunTimeout.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(recovery.TimeoutMs)
            : DefaultRunTimeout;
        var start = Start(new AgentRunRequest
        {
            SessionId = sessionId,
            Messages = recovery.Messages,
            Model = recovery.Snapshot.Model,
            Temperature = recovery.Temperature,
            MaxTokens = recovery.MaxTokens,
            Timeout = timeout
        });
        if (!start.Started)
            return new(false, normalizedRunId, string.Empty, sessionId, start.Error);

        _recoverableRuns.TryRemove(normalizedRunId, out _);
        if (_runHistories.TryGetValue(normalizedRunId, out var previousHistory))
            previousHistory.DisableResume();
        PersistRunState();
        return new(true, normalizedRunId, start.RunId, sessionId);
    }

    public AgentRunApprovalResult Approve(string runId, string toolCallId)
    {
        return DecideApproval(runId, toolCallId, approved: true);
    }

    public AgentRunApprovalResult Deny(string runId, string toolCallId)
    {
        return DecideApproval(runId, toolCallId, approved: false);
    }

    public AgentRunCredentialResult ProvideCredential(
        string runId,
        string credentialRequestId,
        string value,
        bool rememberForRun)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        var normalizedRequestId = credentialRequestId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0 || normalizedRequestId.Length == 0)
            return new(false, normalizedRunId, normalizedRequestId, "A runId and credentialRequestId are required.");
        if (string.IsNullOrEmpty(value))
            return new(false, normalizedRunId, normalizedRequestId, "A credential value is required.");
        if (value.Contains('\r') || value.Contains('\n'))
            return new(false, normalizedRunId, normalizedRequestId, "Credential values cannot contain line breaks.");
        if (value.Length > MaximumCredentialCharacters)
        {
            return new(
                false,
                normalizedRunId,
                normalizedRequestId,
                $"The credential value cannot exceed {MaximumCredentialCharacters} characters.");
        }

        if (!_activeRuns.TryGetValue(normalizedRunId, out var activeRun) ||
            !activeRun.PendingCredentials.TryGetValue(normalizedRequestId, out var pending))
        {
            return new(false, normalizedRunId, normalizedRequestId, "The credential request was not found or has expired.");
        }

        if (pending.IsExpired(DateTimeOffset.UtcNow))
        {
            activeRun.PendingCredentials.TryRemove(normalizedRequestId, out _);
            pending.Response.TrySetResult(null);
            return new(false, normalizedRunId, normalizedRequestId, "The credential request has expired.");
        }

        return pending.Response.TrySetResult(new AgentCredentialValue(value, rememberForRun))
            ? new(true, normalizedRunId, normalizedRequestId)
            : new(false, normalizedRunId, normalizedRequestId, "The credential request has already been handled.");
    }

    public AgentRunCredentialResult DenyCredential(string runId, string credentialRequestId)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        var normalizedRequestId = credentialRequestId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0 || normalizedRequestId.Length == 0)
            return new(false, normalizedRunId, normalizedRequestId, "A runId and credentialRequestId are required.");

        if (!_activeRuns.TryGetValue(normalizedRunId, out var activeRun) ||
            !activeRun.PendingCredentials.TryRemove(normalizedRequestId, out var pending))
        {
            return new(false, normalizedRunId, normalizedRequestId, "The credential request was not found or has expired.");
        }

        return pending.Response.TrySetResult(null)
            ? new(true, normalizedRunId, normalizedRequestId)
            : new(false, normalizedRunId, normalizedRequestId, "The credential request has already been handled.");
    }

    public IReadOnlyList<AgentRuntimeRunSnapshot> GetActiveRuns()
        => _activeRuns.Values
            .OrderBy(run => run.StartedAtUtc)
            .Select(run => run.EventHistory.ToSnapshot())
            .ToList();

    public IReadOnlyList<AgentRuntimeRunSnapshot> GetRecentRuns(int limit = DefaultRunListLimit)
        => _runHistories.Values
            .OrderByDescending(history => history.StartedAtUtc)
            .Take(Math.Clamp(limit, 1, MaximumRetainedRuns))
            .Select(history => history.ToSnapshot())
            .ToList();

    public AgentRuntimeRunSnapshot? GetRun(string runId)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        return normalizedRunId.Length == 0 ||
               !_runHistories.TryGetValue(normalizedRunId, out var history)
            ? null
            : history.ToSnapshot();
    }

    public int ClearCompletedRuns()
    {
        ThrowIfDisposed();
        var cleared = 0;
        var histories = (ICollection<KeyValuePair<string, RunEventHistory>>)_runHistories;
        foreach (var pair in _runHistories.ToArray())
        {
            if (!pair.Value.IsCompleted)
                continue;

            if (histories.Remove(pair))
            {
                _recoverableRuns.TryRemove(pair.Key, out _);
                cleared++;
            }
        }

        if (cleared > 0)
            PersistRunState();
        return cleared;
    }

    public AgentRuntimeRunEventsResult? ReadEvents(
        string runId,
        long afterSequence = 0,
        int limit = DefaultEventReadLimit)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0 || afterSequence < 0)
            return null;

        if (!_runHistories.TryGetValue(normalizedRunId, out var history))
            return null;

        return history.Read(afterSequence, Math.Clamp(limit, 1, MaximumEventReadLimit));
    }

    public IDisposable Subscribe(Action<AgentRuntimeStreamEnvelope> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ThrowIfDisposed();
        lock (_observersGate)
            _observers.Add(observer);
        return new Subscription(this, observer);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var activeRuns = _activeRuns.Values.ToArray();
        foreach (var activeRun in activeRuns)
        {
            activeRun.MarkInterrupted();
            activeRun.EventHistory.MarkInterrupted();
            if (_recoverableRuns.TryGetValue(activeRun.RunId, out var recovery))
            {
                _recoverableRuns[activeRun.RunId] = recovery with
                {
                    Snapshot = activeRun.EventHistory.ToSnapshot()
                };
            }
        }
        PersistRunState();

        foreach (var activeRun in activeRuns)
        {
            try
            {
                activeRun.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _activeRuns.Clear();
        _activeSessionRuns.Clear();
        lock (_observersGate)
            _observers.Clear();
    }

    private async Task ExecuteAsync(
        ActiveRun activeRun,
        AgentRunRequest request,
        AgentProviderSettings provider)
    {
        using var timeoutCancellation = new CancellationTokenSource(request.Timeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            activeRun.Cancellation.Token,
            timeoutCancellation.Token);

        try
        {
            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "run_start",
                    Provider: provider.BuiltinId,
                    Model: string.IsNullOrWhiteSpace(request.Model) ? provider.Model : request.Model));

            EnsureSessionIsConnected(activeRun.SessionId);
            cancellation.Token.ThrowIfCancellationRequested();
            var conversation = request.Messages.ToList();
            var streamedModelResponse = false;
            var loopResult = await _runtimeLoop.ExecuteAsync(
                conversation,
                completeModelAsync: async (_, messages, cancellationToken) =>
                {
                    EnsureSessionIsConnected(activeRun.SessionId);
                    streamedModelResponse = false;
                    var response = await CompleteModelWithRetryAsync(
                            activeRun,
                            provider,
                            new AgentModelRequest(
                                messages,
                                request.Model,
                                request.Temperature,
                                request.MaxTokens,
                                GetToolDefinitions()),
                            cancellationToken,
                            chunk =>
                            {
                                if (string.IsNullOrEmpty(chunk.Text))
                                    return;

                                streamedModelResponse = true;
                                Publish(
                                    activeRun,
                                    new AgentRuntimeStreamEvent(
                                        "text_delta",
                                        Text: chunk.Text,
                                        Provider: provider.BuiltinId,
                                        Model: request.Model ?? provider.Model));
                            })
                        .ConfigureAwait(false);
                    EnsureSessionIsConnected(activeRun.SessionId);
                    return response;
                },
                executeToolAsync: async (toolCall, cancellationToken) =>
                {
                    EnsureSessionIsConnected(activeRun.SessionId);
                    var toolInput = NormalizeToolInput(toolCall.Arguments);
                    var toolStartedAt = DateTimeOffset.UtcNow;
                    Publish(
                        activeRun,
                        new AgentRuntimeStreamEvent(
                            "tool_call_update",
                            ToolCallId: toolCall.Id,
                            ToolName: toolCall.Name,
                            Input: toolInput,
                            Status: "running"));

                    var toolResult = await ExecuteToolAsync(
                            activeRun,
                            toolCall,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);
                    Publish(
                        activeRun,
                        new AgentRuntimeStreamEvent(
                            "tool_call_result",
                            ToolCallId: toolCall.Id,
                            ToolName: toolCall.Name,
                            Result: toolResult.Content,
                            Status: toolResult.IsSuccess ? "completed" : "failed",
                            DurationMs: Math.Max(0, (long)(DateTimeOffset.UtcNow - toolStartedAt).TotalMilliseconds)));
                    return new OpenCoworkRuntimeToolResult(toolResult.IsSuccess, toolResult.Content);
                },
                modelRequestStarted: activeRun.EventHistory.RecordModelRequest,
                modelResponseReceived: response =>
                {
                    if (!streamedModelResponse && !string.IsNullOrEmpty(response.Text))
                    {
                        Publish(
                            activeRun,
                            new AgentRuntimeStreamEvent(
                            "text_delta",
                            Text: response.Text,
                            Provider: response.Provider,
                            Model: response.Model,
                            InputTokens: response.InputTokens,
                            OutputTokens: response.OutputTokens));
                    }
                },
                summarizeContextAsync: (messages, cancellationToken) =>
                    SummarizeContextAsync(
                        activeRun,
                        provider,
                        request,
                        messages,
                        cancellationToken),
                contextCompressed: compression =>
                    Publish(
                        activeRun,
                        new AgentRuntimeStreamEvent(
                            "context_compressed",
                            Message: $"Context compressed from {compression.OriginalMessageCount} " +
                                     $"to {compression.NewMessageCount} messages" +
                                     (compression.UsedFallback ? " using local fallback." : "."))),
                applyPendingMessages: conversation =>
                {
                    foreach (var message in activeRun.DrainPendingMessages())
                    {
                        conversation.Add(message);
                        Publish(
                            activeRun,
                            new AgentRuntimeStreamEvent(
                                "run_message_appended",
                                Message: "A follow-up instruction was added to the Agent run.",
                                Status: "accepted"));
                    }
                },
                stopRequested: () => activeRun.StopRequested,
                cancellationToken: cancellation.Token).ConfigureAwait(false);

            if (loopResult.IsCompleted)
            {
                Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "completed"));
                return;
            }

            if (loopResult.Reason == "stopped")
            {
                Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "stopped"));
                return;
            }

            var (errorType, message) = loopResult.Reason switch
            {
                "model_request_limit" => (
                    "ModelRequestLimit",
                    $"The agent run exceeded the maximum of {MaximumModelRequestsPerRun} model requests."),
                "tool_call_limit" => (
                    "ToolCallLimit",
                    $"The agent run exceeded the maximum of {MaximumToolCallsPerRun} tool calls."),
                "max_iterations" => (
                    "MaxIterations",
                    $"The agent run exceeded the maximum of {MaximumIterations} iterations."),
                _ => ("RuntimeLimit", "The agent runtime stopped because a safety limit was reached.")
            };
            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "error",
                    Message: message,
                    ErrorType: errorType));
            Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "limits"));
        }
        catch (OperationCanceledException) when (activeRun.Cancellation.IsCancellationRequested)
        {
            Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "aborted"));
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "error",
                    Message: "Agent run timed out.",
                    ErrorType: "Timeout"));
            Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "timeout"));
        }
        catch (AgentSessionUnavailableException ex)
        {
            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "error",
                    Message: ex.Message,
                    ErrorType: "SessionUnavailable"));
            Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "session_unavailable"));
        }
        catch (AgentProviderException ex)
        {
            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "error",
                    Message: ex.SafeMessage,
                    ErrorType: ex.Kind.ToString(),
                    Details: ex.SafeMessage,
                    StatusCode: ex.StatusCode));
            Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "provider_error"));
        }
        catch (Exception ex)
        {
            var message = TrimException(ex);
            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "error",
                    Message: message,
                    ErrorType: ex.GetType().Name,
                    Details: message));
            Publish(activeRun, new AgentRuntimeStreamEvent("loop_end", Reason: "error"));
        }
        finally
        {
            if (activeRun.IsInterrupted)
                activeRun.EventHistory.MarkInterrupted();
            else
            {
                activeRun.EventHistory.MarkCompleted();
                _recoverableRuns.TryRemove(activeRun.RunId, out _);
            }
            RemoveActiveRun(activeRun);
            _activeSessionRuns.TryRemove(
                new KeyValuePair<Guid, string>(activeRun.SessionId, activeRun.RunId));
            PruneRunHistories();
            foreach (var pending in activeRun.PendingApprovals.Values)
            {
                _gateway.TryDeny(pending.RequestId);
                pending.Decision.TrySetResult(false);
            }
            activeRun.PendingApprovals.Clear();
            foreach (var pending in activeRun.PendingCredentials.Values)
                pending.Response.TrySetResult(null);
            activeRun.PendingCredentials.Clear();
            activeRun.ClearCredentials();
            activeRun.DisposeEventPublisher();
            activeRun.Cancellation.Dispose();
            PersistRunState();
        }
    }

    private async Task<AgentModelResponse> CompleteModelWithRetryAsync(
        ActiveRun activeRun,
        AgentProviderSettings provider,
        AgentModelRequest request,
        CancellationToken cancellationToken,
        Action<AgentModelStreamChunk>? onStreamChunk = null)
    {
        var delay = TimeSpan.FromMilliseconds(400);
        var streamedOutput = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (onStreamChunk != null && _modelClient is IAgentStreamingModelClient streamingClient)
                {
                    return await streamingClient.CompleteStreamingAsync(
                            provider,
                            request,
                            chunk =>
                            {
                                if (!string.IsNullOrEmpty(chunk.Text) ||
                                    !string.IsNullOrEmpty(chunk.Thinking))
                                {
                                    streamedOutput = true;
                                }

                                onStreamChunk(chunk);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return await _modelClient.CompleteAsync(
                        provider,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentProviderException exception) when (
                attempt < MaximumModelRetryAttempts &&
                exception.Retryable &&
                !streamedOutput &&
                !cancellationToken.IsCancellationRequested)
            {
                var delayMs = (int)delay.TotalMilliseconds;
                Publish(
                    activeRun,
                    new AgentRuntimeStreamEvent(
                        "request_retry",
                        Message: $"{exception.SafeMessage} Retrying the model request.",
                        ErrorType: exception.Kind.ToString(),
                        Attempt: attempt,
                        MaxAttempts: MaximumModelRetryAttempts,
                        DelayMs: delayMs,
                        StatusCode: exception.StatusCode));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 4000));
            }
            catch (Exception exception) when (
                attempt < MaximumModelRetryAttempts &&
                IsTransientModelFailure(exception) &&
                !streamedOutput &&
                !cancellationToken.IsCancellationRequested)
            {
                var statusCode = exception is HttpRequestException httpException &&
                                 httpException.StatusCode.HasValue
                    ? (int)httpException.StatusCode.Value
                    : (int?)null;
                var delayMs = (int)delay.TotalMilliseconds;
                Publish(
                    activeRun,
                    new AgentRuntimeStreamEvent(
                        "request_retry",
                        Message: "A temporary provider error occurred; retrying the model request.",
                        ErrorType: exception.GetType().Name,
                        Attempt: attempt,
                        MaxAttempts: MaximumModelRetryAttempts,
                        DelayMs: delayMs,
                        StatusCode: statusCode));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 4000));
            }
        }
    }

    private async Task<string?> SummarizeContextAsync(
        ActiveRun activeRun,
        AgentProviderSettings provider,
        AgentRunRequest request,
        IReadOnlyList<AgentChatMessage> messages,
        CancellationToken cancellationToken)
    {
        using var summaryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        summaryTimeout.CancelAfter(
            TimeSpan.FromSeconds(OpenCoworkRuntimeContextCompactor.SummaryTimeoutSeconds));

        var summaryRequest = new AgentModelRequest(
            [
                new AgentChatMessage(
                    "system",
                    "You compress long operations conversations into durable working memory. " +
                    "Preserve exact user intent, constraints, decisions, commands, errors, " +
                    "verified results, and unfinished work. Omit filler. Return only a concise " +
                    "plain-text summary."),
                new AgentChatMessage(
                    "user",
                    OpenCoworkRuntimeContextCompactor.BuildSummaryPrompt(messages))
            ],
            request.Model,
            Temperature: 0.1,
            MaxTokens: 2000);

        try
        {
            var response = await CompleteModelWithRetryAsync(
                    activeRun,
                    provider,
                    summaryRequest,
                    summaryTimeout.Token)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTransientModelFailure(Exception exception)
    {
        if (exception is IOException)
            return true;

        if (exception is not HttpRequestException httpException)
            return false;

        return !httpException.StatusCode.HasValue ||
               httpException.StatusCode is System.Net.HttpStatusCode.RequestTimeout or
                   System.Net.HttpStatusCode.TooManyRequests ||
               (int)httpException.StatusCode.Value >= 500;
    }

    private void EnsureSessionIsConnected(Guid sessionId)
    {
        var session = _gateway.GetSession(sessionId);
        if (session == null || !session.IsConnected)
        {
            throw new AgentSessionUnavailableException(
                "The SSH session was disconnected while the Agent run was active.");
        }
    }

    private async Task<AgentToolExecutionResult> ExecuteToolAsync(
        ActiveRun activeRun,
        AgentToolCall toolCall,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateToolCall(toolCall, out var toolCallError))
            return new(false, toolCallError);

        if (string.Equals(toolCall.Name, SessionInfoToolName, StringComparison.Ordinal))
        {
            var session = _gateway.GetSession(request.SessionId);
            return session == null
                ? new(false, "The selected SSH session is no longer available.")
                : new(true, JsonSerializer.Serialize(new
                {
                    sessionId = session.SessionId.ToString("D"),
                    session.Name,
                    session.Protocol,
                    session.Host,
                    session.Port,
                    session.Username,
                    session.Platform,
                    session.IsConnected,
                    canExecuteCommands = session.CanExecuteCommands
                }));
        }

        if (string.Equals(toolCall.Name, DiagnosticRunToolName, StringComparison.Ordinal))
            return await ExecuteDiagnosticAsync(activeRun, toolCall, request, cancellationToken)
                .ConfigureAwait(false);

        if (string.Equals(toolCall.Name, RunbookRunToolName, StringComparison.Ordinal))
            return await ExecuteRunbookAsync(activeRun, toolCall, request, cancellationToken)
                .ConfigureAwait(false);

        if (string.Equals(toolCall.Name, FleetDiagnosticToolName, StringComparison.Ordinal))
            return await ExecuteFleetDiagnosticAsync(toolCall, cancellationToken)
                .ConfigureAwait(false);

        if (toolCall.Name is AgentReadOnlyToolCatalog.LogsToolName or
            AgentReadOnlyToolCatalog.PortCheckToolName or
            AgentReadOnlyToolCatalog.ServiceDetailToolName or
            AgentReadOnlyToolCatalog.FilePreviewToolName or
            AgentReadOnlyToolCatalog.PackageQueryToolName or
            AgentReadOnlyToolCatalog.RuntimeCheckToolName or
            AgentReadOnlyToolCatalog.DiskCleanupAdviceToolName)
        {
            return await ExecuteReadOnlyToolAsync(
                    activeRun,
                    toolCall,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(toolCall.Name, SessionCommandToolName, StringComparison.Ordinal))
        {
            return new(
                false,
                $"Unknown tool '{toolCall.Name}'. Available tools: {SessionInfoToolName}, {DiagnosticRunToolName}, {RunbookRunToolName}, {FleetDiagnosticToolName}, {AgentReadOnlyToolCatalog.LogsToolName}, {AgentReadOnlyToolCatalog.PortCheckToolName}, {AgentReadOnlyToolCatalog.ServiceDetailToolName}, {AgentReadOnlyToolCatalog.FilePreviewToolName}, {AgentReadOnlyToolCatalog.PackageQueryToolName}, {AgentReadOnlyToolCatalog.RuntimeCheckToolName}, {AgentReadOnlyToolCatalog.DiskCleanupAdviceToolName}, {SessionCommandToolName}.");
        }

        if (!TryReadToolArguments(
                toolCall.Arguments,
                out var command,
                out var timeoutMs,
                out var hasExplicitTimeout,
                out var error))
            return new(false, error!);

        timeoutMs = (int)AgentCommandTimeoutPolicy.Resolve(
                command,
                TimeSpan.FromMilliseconds(timeoutMs),
                hasExplicitTimeout)
            .TotalMilliseconds;

        var commandRequest = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = request.SessionId,
            Command = command,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            AppendLineEnding = true
        };
        var result = await _gateway.ExecuteCommandAsync(commandRequest, cancellationToken).ConfigureAwait(false);
        var approvalGranted = false;

        if (result.ApprovalRequired)
        {
            var pending = new PendingToolApproval(toolCall.Id, result.RequestId);
            if (!activeRun.PendingApprovals.TryAdd(toolCall.Id, pending))
                return new(false, "This tool call already has a pending approval request.");

            Publish(
                activeRun,
                new AgentRuntimeStreamEvent(
                    "tool_call_approval_required",
                    ToolCallId: toolCall.Id,
                    ToolName: toolCall.Name,
                    Input: NormalizeToolInput(toolCall.Arguments),
                    Message: "This command requires explicit approval before it can be sent.",
                    Status: "pending_approval",
                    Risk: result.Risk.ToString(),
                    TimeoutMs: timeoutMs,
                    SessionName: _gateway.GetSession(request.SessionId)?.Name));

            try
            {
                var approved = await pending.Decision.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!approved || string.IsNullOrWhiteSpace(pending.ApprovalToken))
                {
                    return new(false, "The command approval was denied.");
                }

                result = await _gateway.ExecuteCommandAsync(
                    commandRequest with
                    {
                        ApprovalToken = pending.ApprovalToken,
                        ApprovalGranted = true
                    },
                    cancellationToken).ConfigureAwait(false);
                approvalGranted = true;
            }
            finally
            {
                _gateway.TryDeny(pending.RequestId);
                activeRun.PendingApprovals.TryRemove(toolCall.Id, out _);
            }
        }

        string? sensitiveInput = null;
        var sensitiveInputs = new List<string>();
        var credentialAttempt = 0;
        while (TryGetCredentialRequirement(command, result, out var credentialRequirement))
        {
            credentialAttempt++;
            if (credentialAttempt > MaximumCredentialAttempts)
                break;

            var credentialRemembered = false;
            var credentialWasCached = activeRun.TryGetCredential(
                credentialRequirement.Kind,
                out var cachedCredential);
            if (credentialWasCached)
            {
                sensitiveInput = cachedCredential;
            }
            else
            {
                var credentialRequestId = Guid.NewGuid().ToString("N");
                var pending = new PendingCredential(
                    credentialRequestId,
                    toolCall.Id,
                    credentialRequirement.Kind,
                    credentialRequirement.Prompt);
                if (!activeRun.PendingCredentials.TryAdd(credentialRequestId, pending))
                    return new(false, "The credential request could not be created.");

                Publish(
                    activeRun,
                    new AgentRuntimeStreamEvent(
                        "credential_required",
                        ToolCallId: toolCall.Id,
                        ToolName: toolCall.Name,
                        CredentialRequestId: credentialRequestId,
                        CredentialKind: pending.Kind,
                        CredentialPrompt: pending.Prompt,
                        Message: $"The remote command requires {pending.Kind} input.",
                        Status: "pending_credential",
                        Attempt: credentialAttempt,
                        MaxAttempts: MaximumCredentialAttempts,
                        TimeoutMs: timeoutMs,
                        SessionName: _gateway.GetSession(request.SessionId)?.Name));

                try
                {
                    AgentCredentialValue? credential;
                    try
                    {
                        credential = await pending.Response.Task
                            .WaitAsync(CredentialRequestLifetime, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        return new(false, "Credential input expired before it was submitted.");
                    }
                    if (credential == null)
                        return new(false, "Credential input was cancelled.");

                    sensitiveInput = credential.Value;
                    credentialRemembered = credential.RememberForRun;
                    if (credentialRemembered)
                        activeRun.RememberCredential(pending.Kind, credential.Value);
                }
                finally
                {
                    activeRun.PendingCredentials.TryRemove(credentialRequestId, out _);
                }
            }

            if (!string.IsNullOrEmpty(sensitiveInput))
                sensitiveInputs.Add(sensitiveInput);

            var credentialCommand = PrepareCredentialCommand(command, credentialRequirement.Kind);
            result = await _gateway.ExecuteCommandAsync(
                    commandRequest with
                    {
                        RequestId = Guid.NewGuid(),
                        Command = credentialCommand,
                        SensitiveInput = sensitiveInput,
                        ApprovalGranted = approvalGranted,
                        ApprovedCommand = approvalGranted ? command : null
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (TryGetCredentialRequirement(command, result, out var rejectedCredential))
            {
                // A cached or just-entered credential was rejected. Forget it
                // before asking again so the next attempt cannot repeat it.
                if (credentialWasCached || credentialRemembered)
                    activeRun.RemoveCredential(rejectedCredential.Kind);

                sensitiveInput = null;
                continue;
            }

            break;
        }

        var repeatedFailureGuidance = activeRun.RecordCommandOutcome(command, result.IsSuccess);
        return new(
            result.IsSuccess,
            SerializeCommandResult(activeRun, result, repeatedFailureGuidance, sensitiveInputs));
    }

    private static bool TryGetCredentialRequirement(
        string command,
        AgentCommandResult result,
        out CredentialRequirement requirement)
    {
        requirement = default!;

        var text = string.Join(
            Environment.NewLine,
            result.Message ?? string.Empty,
            result.Output ?? string.Empty);

        // A command endpoint normally reports a non-zero exit code as a
        // failed result. Keep the narrow success-path check for endpoints
        // that only return captured text, so a normal command that prints
        // "password:" is not mistaken for an interactive prompt.
        var hasExplicitPasswordFailure = ContainsAny(
            text,
            "sudo: a password is required",
            "sudo: no password was provided",
            "sudo: a terminal is required",
            "sudo: no tty present",
            "sudo: no askpass program specified");
        if (result.IsSuccess &&
            (!hasExplicitPasswordFailure || !ContainsAny(command, "sudo", "doas")))
            return false;

        if (ContainsAny(text, "username:", "user name:", "login:", "login as:"))
        {
            requirement = new(
                "username",
                "Enter the username required by the remote command.");
            return true;
        }

        if (ContainsAny(text, "verification code", "one-time code", "one-time password", "otp:", "token:"))
        {
            requirement = new(
                "token",
                "Enter the verification code or token required by the remote command.");
            return true;
        }

        if (ContainsAny(
                text,
                "password is required",
                "a terminal is required",
                "no tty present",
                "askpass",
                "passcode",
                "password:",
                "enter password"))
        {
            requirement = new(
                "password",
                "Enter the password required by the remote command.");
            return true;
        }

        if (ContainsAny(
                text,
                "sorry, try again",
                "incorrect password attempt",
                "authentication failure",
                "authentication failed") &&
            ContainsAny(command, "sudo", "doas"))
        {
            requirement = new(
                "password",
                "The password was rejected. Enter the password required by the remote command.");
            return true;
        }

        return false;
    }

    private static string PrepareCredentialCommand(string command, string credentialKind)
    {
        if (!string.Equals(credentialKind, "password", StringComparison.OrdinalIgnoreCase))
            return command;

        if (CountSudoInvocations(command) == 0)
            return command;

        var nonInteractiveSudo = System.Text.RegularExpressions.Regex.Replace(
            command,
            @"(?<![A-Za-z0-9_-])sudo\s+(?:-n|--non-interactive)(?=\s|$)",
            "sudo -n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        nonInteractiveSudo = System.Text.RegularExpressions.Regex.Replace(
            nonInteractiveSudo,
            @"(?<![A-Za-z0-9_-])sudo(?!\s+(?:-n|--non-interactive)(?=\s|$))(?=\s|$)",
            "sudo -n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        // Keep the password-consuming sudo and the privileged command in the
        // same sudo process. This also works when timestamp_timeout is zero,
        // because a second non-interactive sudo is not needed.
        var commandWithoutSudo = System.Text.RegularExpressions.Regex.Replace(
            nonInteractiveSudo,
            @"(?<![A-Za-z0-9_-])sudo\s+-n\s+",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return "sudo -S -p '' sh -c " + QuotePosixShellArgument(commandWithoutSudo);
    }

    private static int CountSudoInvocations(string command)
        => System.Text.RegularExpressions.Regex.Matches(
                command,
                @"(?<![A-Za-z0-9_-])sudo(?=\s|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1))
            .Count;

    private static string QuotePosixShellArgument(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool TryValidateToolCall(
        AgentToolCall? toolCall,
        out string error)
    {
        error = string.Empty;
        if (toolCall == null)
        {
            error = "The model returned an empty tool call.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(toolCall.Id))
        {
            error = "The model returned a tool call without an ID.";
            return false;
        }

        if (toolCall.Id.Length > MaximumToolCallIdCharacters)
        {
            error = $"Tool call ID cannot exceed {MaximumToolCallIdCharacters} characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(toolCall.Name))
        {
            error = "The model returned a tool call without a name.";
            return false;
        }

        if (toolCall.Name.Length > MaximumToolNameCharacters)
        {
            error = $"Tool name cannot exceed {MaximumToolNameCharacters} characters.";
            return false;
        }

        if (toolCall.Arguments?.Length > MaximumToolArgumentsCharacters)
        {
            error = $"Tool arguments cannot exceed {MaximumToolArgumentsCharacters} characters.";
            return false;
        }

        return true;
    }

    private async Task<AgentToolExecutionResult> ExecuteDiagnosticAsync(
        ActiveRun activeRun,
        AgentToolCall toolCall,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadDiagnosticScope(toolCall.Arguments, out var scope, out var error))
            return new(false, error!);

        var session = _gateway.GetSession(request.SessionId);
        if (session == null)
            return new(false, "The selected SSH session is no longer available.");

        if (!AgentDiagnosticCatalog.TryCreatePlan(session, scope, out var plan, out error))
            return new(false, error!);

        var commandRequest = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = request.SessionId,
            Command = plan.Command,
            DisplayCommand = plan.DisplayCommand,
            Timeout = plan.Timeout,
            AppendLineEnding = true
        };
        var result = await _gateway.ExecuteCommandAsync(commandRequest, cancellationToken)
            .ConfigureAwait(false);
        if (result.ApprovalRequired)
        {
            return new(
                false,
                JsonSerializer.Serialize(new
                {
                    diagnostic = plan.Scope,
                    platform = plan.Platform,
                    status = result.Status.ToString(),
                    message = "The diagnostic was blocked by the command permission policy.",
                    approvalRequired = true
                }));
        }

        return new(
            result.IsSuccess,
            JsonSerializer.Serialize(new
            {
                diagnostic = plan.Scope,
                platform = plan.Platform,
                displayCommand = plan.DisplayCommand,
                status = result.Status.ToString(),
                message = result.Message,
                requestId = result.RequestId.ToString("D"),
                sessionId = activeRun.SessionId.ToString("D"),
                remoteCompletionConfirmed = result.RemoteCompletionConfirmed,
                output = LimitToolResultOutput(result.Output)
            }));
    }

    private async Task<AgentToolExecutionResult> ExecuteReadOnlyToolAsync(
        ActiveRun activeRun,
        AgentToolCall toolCall,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        var session = _gateway.GetSession(request.SessionId);
        if (session == null)
            return new(false, "The selected SSH session is no longer available.");

        using var arguments = JsonDocument.Parse(toolCall.Arguments ?? "{}");
        if (!AgentReadOnlyToolCatalog.TryCreatePlan(
                session,
                toolCall.Name,
                arguments.RootElement,
                out var plan,
                out var error))
        {
            return new(false, error ?? "The read-only tool arguments are invalid.");
        }

        var commandRequest = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = request.SessionId,
            Command = plan.Command,
            DisplayCommand = plan.DisplayCommand,
            Timeout = plan.Timeout,
            AppendLineEnding = true
        };
        var result = await _gateway.ExecuteCommandAsync(commandRequest, cancellationToken)
            .ConfigureAwait(false);
        return new(
            result.IsSuccess,
            JsonSerializer.Serialize(new
            {
                tool = toolCall.Name,
                platform = plan.Platform,
                displayCommand = plan.DisplayCommand,
                status = result.Status.ToString(),
                message = result.Message,
                requestId = result.RequestId.ToString("D"),
                sessionId = activeRun.SessionId.ToString("D"),
                remoteCompletionConfirmed = result.RemoteCompletionConfirmed,
                output = LimitToolResultOutput(result.Output)
            }));
    }

    private async Task<AgentToolExecutionResult> ExecuteRunbookAsync(
        ActiveRun activeRun,
        AgentToolCall toolCall,
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadScopeArguments(
                toolCall.Arguments,
                "runbook_run",
                "scope",
                out var scope,
                out var error))
        {
            return new(false, error!);
        }

        var session = _gateway.GetSession(request.SessionId);
        if (session == null)
            return new(false, "The selected SSH session is no longer available.");

        if (!AgentDiagnosticRunbookCatalog.TryCreatePlan(session, scope, out var plan, out error))
            return new(false, error!);

        var commandRequest = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = request.SessionId,
            Command = plan.Command,
            DisplayCommand = plan.DisplayCommand,
            Timeout = plan.Timeout,
            AppendLineEnding = true
        };
        var result = await _gateway.ExecuteCommandAsync(commandRequest, cancellationToken)
            .ConfigureAwait(false);

        return new(
            result.IsSuccess,
            JsonSerializer.Serialize(new
            {
                runbook = plan.Scope,
                platform = plan.Platform,
                displayCommand = plan.DisplayCommand,
                status = result.Status.ToString(),
                message = result.Message,
                requestId = result.RequestId.ToString("D"),
                sessionId = activeRun.SessionId.ToString("D"),
                remoteCompletionConfirmed = result.RemoteCompletionConfirmed,
                output = LimitToolResultOutput(result.Output)
            }));
    }

    private async Task<AgentToolExecutionResult> ExecuteFleetDiagnosticAsync(
        AgentToolCall toolCall,
        CancellationToken cancellationToken)
    {
        if (!TryReadScopeArguments(
                toolCall.Arguments,
                FleetDiagnosticToolName,
                "scope",
                out var scope,
                out var error))
        {
            return new(false, error!);
        }

        var result = await _gateway.RunReadOnlyDiagnosticAcrossSessionsAsync(
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            result.FailureCount == 0,
            JsonSerializer.Serialize(new
            {
                fleet = true,
                diagnostic = result.Scope,
                targetCount = result.TargetCount,
                successCount = result.SuccessCount,
                failureCount = result.FailureCount,
                results = result.Results.Select(item => new
                {
                    sessionId = item.SessionId.ToString("D"),
                    name = item.Name,
                    host = item.Host,
                    platform = item.Platform,
                    status = item.Status,
                    message = item.Message,
                    remoteCompletionConfirmed = item.RemoteCompletionConfirmed,
                    output = LimitFleetOutput(item.Output)
                })
            }));
    }

    private static string? LimitFleetOutput(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        const int maximumPerSession = 16 * 1024;
        return output.Length <= maximumPerSession
            ? output
            : output[..maximumPerSession] + "\n[fleet output truncated by CxShell]";
    }

    private static string SerializeCommandResult(
        ActiveRun activeRun,
        AgentCommandResult result,
        string? agentGuidance = null,
        IReadOnlyList<string>? sensitiveInputs = null)
    {
        var message = result.Message;
        var output = result.Output;
        foreach (var sensitiveInput in (sensitiveInputs ?? Array.Empty<string>())
                     .Where(value => !string.IsNullOrEmpty(value))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(value => value.Length))
        {
            message = message?.Replace(sensitiveInput, "[redacted]", StringComparison.Ordinal);
            output = output?.Replace(sensitiveInput, "[redacted]", StringComparison.Ordinal);
        }

        return JsonSerializer.Serialize(new
        {
            status = result.Status.ToString(),
            message,
            requestId = result.RequestId.ToString("D"),
            sessionId = activeRun.SessionId.ToString("D"),
            remoteCompletionConfirmed = result.RemoteCompletionConfirmed,
            risk = result.Risk.ToString(),
            executionState = result.ExecutionState.ToString(),
            outcomeCertain = result.IsOutcomeCertain,
            retrySafe = result.IsRetrySafe,
            output = LimitToolResultOutput(output),
            agentGuidance
        });
    }

    private static string? LimitToolResultOutput(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= MaximumToolResultCharacters)
            return output;

        return output[..MaximumToolResultCharacters] + "\n[tool output truncated by CxShell]";
    }

    private static bool TryReadToolArguments(
        string? arguments,
        out string command,
        out int timeoutMs,
        out bool hasExplicitTimeout,
        out string? error)
    {
        command = string.Empty;
        timeoutMs = (int)AgentSessionGateway.DefaultCommandTimeout.TotalMilliseconds;
        hasExplicitTimeout = false;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(arguments ?? "{}");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("command", out var commandElement) ||
                commandElement.ValueKind != JsonValueKind.String)
            {
                error = "session_command requires a non-empty command.";
                return false;
            }

            var parsedCommand = commandElement.GetString();
            if (string.IsNullOrWhiteSpace(parsedCommand))
            {
                error = "session_command requires a non-empty command.";
                return false;
            }

            command = parsedCommand;

            if (root.TryGetProperty("timeoutMs", out var timeoutElement) &&
                timeoutElement.TryGetInt32(out var requestedTimeout))
            {
                hasExplicitTimeout = true;
                timeoutMs = requestedTimeout;
            }

            timeoutMs = Math.Clamp(
                timeoutMs,
                (int)TimeSpan.FromMilliseconds(100).TotalMilliseconds,
                (int)AgentSessionGateway.MaximumCommandTimeout.TotalMilliseconds);
            return true;
        }
        catch (JsonException)
        {
            error = "session_command arguments must be a valid JSON object.";
            return false;
        }
    }

    private static bool TryReadDiagnosticScope(
        string? arguments,
        out string scope,
        out string? error)
    {
        scope = string.Empty;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(arguments ?? "{}");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("scope", out var scopeElement) ||
                scopeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(scopeElement.GetString()))
            {
                error = "diagnostic_run requires a scope.";
                return false;
            }

            scope = scopeElement.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            error = "diagnostic_run arguments must be a valid JSON object.";
            return false;
        }
    }

    private static bool TryReadScopeArguments(
        string? arguments,
        string toolName,
        string propertyName,
        out string scope,
        out string? error)
    {
        scope = string.Empty;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(arguments ?? "{}");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(propertyName, out var scopeElement) ||
                scopeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(scopeElement.GetString()))
            {
                error = $"{toolName} requires a {propertyName}.";
                return false;
            }

            scope = scopeElement.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            error = $"{toolName} arguments must be a valid JSON object.";
            return false;
        }
    }

    private static string NormalizeToolInput(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "{}";

        return arguments.Length <= 16 * 1024 ? arguments : arguments[..(16 * 1024)] + "...";
    }

    private void Publish(ActiveRun activeRun, AgentRuntimeStreamEvent @event)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (activeRun.EventPublishGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (string.Equals(@event.Type, "text_delta", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(@event.Text))
            {
                // A timer is useful for idle streams, but a busy thread pool
                // may delay it. Check the elapsed window when the next chunk
                // arrives so chunk boundaries remain deterministic as well.
                if (activeRun.ShouldFlushTextDeltaBeforeAppend())
                    PublishPendingTextDeltaLocked(activeRun);

                var flushImmediately = activeRun.BufferTextDelta(
                    @event,
                    () => FlushPendingTextDelta(activeRun));
                if (!flushImmediately)
                    return;

                PublishPendingTextDeltaLocked(activeRun);
                return;
            }

            // A tool, error, or lifecycle event is a boundary. Flush text first
            // so the consumer never observes events out of model order.
            PublishPendingTextDeltaLocked(activeRun);
            PublishCoreLocked(activeRun, @event);
        }
    }

    private void FlushPendingTextDelta(ActiveRun activeRun)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (activeRun.EventPublishGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            PublishPendingTextDeltaLocked(activeRun);
        }
    }

    private void PublishPendingTextDeltaLocked(ActiveRun activeRun)
    {
        var textDelta = activeRun.TakePendingTextDelta();
        if (textDelta != null)
            PublishCoreLocked(activeRun, textDelta);
    }

    private void PublishCoreLocked(ActiveRun activeRun, AgentRuntimeStreamEvent @event)
    {
        var envelope = new AgentRuntimeStreamEnvelope(
            1,
            activeRun.RunId,
            activeRun.SessionId.ToString("D"),
            Interlocked.Increment(ref activeRun.Sequence),
            [@event]);
        activeRun.EventHistory.Add(envelope);
        Action<AgentRuntimeStreamEnvelope>[] observers;
        lock (_observersGate)
            observers = _observers.ToArray();

        foreach (var observer in observers)
        {
            try
            {
                observer(envelope);
            }
            catch
            {
                // A consumer must not terminate the model run or affect event ordering.
            }
        }
    }

    private string NormalizeRunId(string? runId)
    {
        var normalized = runId?.Trim();
        if (!string.IsNullOrEmpty(normalized))
            return normalized;

        var next = Interlocked.Increment(ref _generatedRunId);
        return $"cxshell-agent-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{next}";
    }

    private static bool IsValidTimeout(TimeSpan timeout)
        => timeout >= TimeSpan.FromMilliseconds(100) && timeout <= MaximumRunTimeout;

    private static string TrimException(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500] + "...";
    }

    private static string? BuildPromptPreview(IReadOnlyList<AgentChatMessage> messages)
    {
        var prompt = messages.LastOrDefault(message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var normalized = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    private static AgentRunRecoveryState CreateRecoveryState(
        ActiveRun activeRun,
        AgentRunRequest request)
        => new(
            activeRun.EventHistory.ToSnapshot(),
            BuildRecoveryMessages(request.Messages),
            request.Temperature,
            request.MaxTokens,
            (int)request.Timeout.TotalMilliseconds,
            DateTimeOffset.UtcNow + RecoveryLifetime);

    private static IReadOnlyList<AgentChatMessage> BuildRecoveryMessages(
        IReadOnlyList<AgentChatMessage> messages)
        => messages
            .Where(message =>
                message != null &&
                message.Role is not null &&
                (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)) &&
                HasMessageContent(message))
            .Take(AgentRuntimeContract.MaximumMessageCount)
            .Select(message => new AgentChatMessage(
                message.Role,
                message.Content.Length <= AgentRuntimeContract.MaximumMessageCharacters
                    ? message.Content
                    : message.Content[..AgentRuntimeContract.MaximumMessageCharacters],
                ContentParts: message.ContentParts))
            .ToArray();

    private static bool HasMessageContent(AgentChatMessage message)
        => !string.IsNullOrWhiteSpace(message.Content) ||
           message.ContentParts is { Count: > 0 };

    private void PersistRunState()
    {
        PersistRunHistories();
        PersistRecoverableRuns();
    }

    private void PersistRunHistories()
    {
        try
        {
            _historyStore.Save(_runHistories.Values
                .Select(history => history.ToSnapshot())
                .ToArray());
        }
        catch
        {
            // Persistence is best-effort and must not affect a running Agent.
        }
    }

    private void PersistRecoverableRuns()
    {
        try
        {
            _historyStore.SaveRecoverable(_recoverableRuns.Values.ToArray());
        }
        catch
        {
            // Recovery is best-effort and must not affect a running Agent.
        }
    }

    private void Unsubscribe(Action<AgentRuntimeStreamEnvelope> observer)
    {
        lock (_observersGate)
            _observers.Remove(observer);
    }

    private void PruneRunHistories()
    {
        if (_runHistories.Count <= MaximumRetainedRuns)
            return;

        var histories = (ICollection<KeyValuePair<string, RunEventHistory>>)_runHistories;
        foreach (var pair in _runHistories.OrderBy(item => item.Value.StartedAtUtc))
        {
            if (_runHistories.Count <= MaximumRetainedRuns)
                break;
            if (!pair.Value.IsCompleted)
                continue;

            // Remove only the exact key/value pair so a newly reused runId is not removed.
            if (histories.Remove(pair))
                _recoverableRuns.TryRemove(pair.Key, out _);
        }
    }

    private void RemoveActiveRun(ActiveRun activeRun)
    {
        var activeRuns = (ICollection<KeyValuePair<string, ActiveRun>>)_activeRuns;
        activeRuns.Remove(new KeyValuePair<string, ActiveRun>(activeRun.RunId, activeRun));
    }

    private sealed class AgentSessionUnavailableException : Exception
    {
        public AgentSessionUnavailableException(string message)
            : base(message)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AgentRunCoordinator));
    }

    private AgentRunApprovalResult DecideApproval(
        string runId,
        string toolCallId,
        bool approved)
    {
        ThrowIfDisposed();
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        var normalizedToolCallId = toolCallId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0 || normalizedToolCallId.Length == 0)
        {
            return new(
                false,
                false,
                normalizedRunId,
                normalizedToolCallId,
                "A runId and toolCallId are required.");
        }

        if (!_activeRuns.TryGetValue(normalizedRunId, out var activeRun) ||
            !activeRun.PendingApprovals.TryGetValue(normalizedToolCallId, out var pending))
        {
            return new(
                false,
                false,
                normalizedRunId,
                normalizedToolCallId,
                "The approval request was not found or has already completed.");
        }

        if (approved)
        {
            if (!_gateway.TryApprove(pending.RequestId, out var approvalToken))
            {
                pending.Decision.TrySetResult(false);
                return new(
                    false,
                    false,
                    normalizedRunId,
                    normalizedToolCallId,
                    "The approval request was not found or has expired.");
            }

            pending.ApprovalToken = approvalToken;
            pending.Decision.TrySetResult(true);
            return new(true, true, normalizedRunId, normalizedToolCallId);
        }

        _gateway.TryDeny(pending.RequestId);
        pending.Decision.TrySetResult(false);
        return new(true, false, normalizedRunId, normalizedToolCallId);
    }

    private sealed class ActiveRun
    {
        public ActiveRun(
            string runId,
            Guid sessionId,
            string provider,
            string model,
            string? promptPreview)
        {
            RunId = runId;
            SessionId = sessionId;
            StartedAtUtc = DateTimeOffset.UtcNow;
            EventHistory = new RunEventHistory(
                runId,
                sessionId,
                StartedAtUtc,
                provider,
                model,
                promptPreview,
                canResume: true);
        }

        public string RunId { get; }
        public Guid SessionId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public RunEventHistory EventHistory { get; }
        public long Sequence;
        public ConcurrentDictionary<string, PendingToolApproval> PendingApprovals { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, PendingCredential> PendingCredentials { get; } = new(StringComparer.Ordinal);
        public object EventPublishGate { get; } = new();

        private readonly object _credentialGate = new();
        private readonly Dictionary<string, string> _rememberedCredentials = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _pendingMessagesGate = new();
        private readonly Queue<AgentChatMessage> _pendingMessages = new();
        private readonly StringBuilder _pendingTextDelta = new();
        private AgentRuntimeStreamEvent? _pendingTextDeltaTemplate;
        private Timer? _textDeltaFlushTimer;
        private DateTimeOffset? _pendingTextDeltaStartedAtUtc;
        private int _stopRequested;
        private int _interrupted;
        private string? _lastCommand;
        private int _consecutiveCommandFailures;

        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;
        public bool IsInterrupted => Volatile.Read(ref _interrupted) != 0;

        public bool TryGetCredential(string kind, out string value)
        {
            lock (_credentialGate)
                return _rememberedCredentials.TryGetValue(kind, out value!);
        }

        public void RememberCredential(string kind, string value)
        {
            lock (_credentialGate)
                _rememberedCredentials[kind] = value;
        }

        public void RemoveCredential(string kind)
        {
            lock (_credentialGate)
                _rememberedCredentials.Remove(kind);
        }

        public void ClearCredentials()
        {
            lock (_credentialGate)
                _rememberedCredentials.Clear();
        }

        public void MarkInterrupted()
            => Volatile.Write(ref _interrupted, 1);

        public bool BufferTextDelta(
            AgentRuntimeStreamEvent @event,
            Action flushCallback)
        {
            ArgumentNullException.ThrowIfNull(flushCallback);
            if (string.IsNullOrEmpty(@event.Text))
                return false;

            if (_pendingTextDelta.Length == 0)
            {
                _pendingTextDeltaTemplate = @event;
                _pendingTextDeltaStartedAtUtc = DateTimeOffset.UtcNow;
                _textDeltaFlushTimer = new Timer(
                    _ => flushCallback(),
                    null,
                    StreamTextDeltaBatchInterval,
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _pendingTextDeltaTemplate = MergeTextDeltaMetadata(
                    _pendingTextDeltaTemplate!,
                    @event);
            }

            _pendingTextDelta.Append(@event.Text);
            return _pendingTextDelta.Length >= MaximumStreamTextDeltaBatchCharacters;
        }

        public AgentRuntimeStreamEvent? TakePendingTextDelta()
        {
            if (_pendingTextDelta.Length == 0 || _pendingTextDeltaTemplate == null)
                return null;

            var textDelta = _pendingTextDeltaTemplate with
            {
                Text = _pendingTextDelta.ToString()
            };
            _pendingTextDelta.Clear();
            _pendingTextDeltaTemplate = null;
            _pendingTextDeltaStartedAtUtc = null;
            _textDeltaFlushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return textDelta;
        }

        public bool ShouldFlushTextDeltaBeforeAppend()
            => _pendingTextDelta.Length > 0 &&
               _pendingTextDeltaStartedAtUtc is { } startedAt &&
               DateTimeOffset.UtcNow - startedAt >= StreamTextDeltaBatchInterval;

        public void DisposeEventPublisher()
        {
            lock (EventPublishGate)
            {
                _textDeltaFlushTimer?.Dispose();
                _textDeltaFlushTimer = null;
                _pendingTextDelta.Clear();
                _pendingTextDeltaTemplate = null;
                _pendingTextDeltaStartedAtUtc = null;
            }
        }

        private static AgentRuntimeStreamEvent MergeTextDeltaMetadata(
            AgentRuntimeStreamEvent current,
            AgentRuntimeStreamEvent next)
            => current with
            {
                Provider = current.Provider ?? next.Provider,
                Model = current.Model ?? next.Model,
                InputTokens = next.InputTokens ?? current.InputTokens,
                OutputTokens = next.OutputTokens ?? current.OutputTokens
            };

        public void RequestStop()
        {
            lock (_pendingMessagesGate)
                Volatile.Write(ref _stopRequested, 1);
        }

        public bool TryQueueMessages(
            IReadOnlyList<AgentChatMessage> messages,
            out string? error)
        {
            lock (_pendingMessagesGate)
            {
                if (StopRequested)
                {
                    error = "The Agent run is stopping and cannot accept more messages.";
                    return false;
                }

                if (_pendingMessages.Count + messages.Count > MaximumAppendedMessagesPerRun)
                {
                    error =
                        $"The Agent run can queue at most {MaximumAppendedMessagesPerRun} follow-up messages.";
                    return false;
                }

                foreach (var message in messages)
                    _pendingMessages.Enqueue(message);
            }

            error = null;
            return true;
        }

        public IReadOnlyList<AgentChatMessage> DrainPendingMessages()
        {
            lock (_pendingMessagesGate)
            {
                if (_pendingMessages.Count == 0)
                    return [];

                var messages = _pendingMessages.ToArray();
                _pendingMessages.Clear();
                return messages;
            }
        }

        public string? RecordCommandOutcome(string command, bool succeeded)
        {
            if (succeeded)
            {
                _lastCommand = null;
                _consecutiveCommandFailures = 0;
                return null;
            }

            if (!string.Equals(_lastCommand, command, StringComparison.Ordinal))
            {
                _lastCommand = command;
                _consecutiveCommandFailures = 1;
                return null;
            }

            _consecutiveCommandFailures++;
            return _consecutiveCommandFailures < 2
                ? null
                : $"The same command has failed {_consecutiveCommandFailures} times in a row. " +
                  "Do not retry it unchanged. Check the error and choose a different approach, " +
                  "such as a reachable download source, package mirror, proxy, or an alternative installer.";
        }
    }

    private sealed class RunEventHistory
    {
        private readonly object _gate = new();
        private readonly Queue<AgentRuntimeStreamEnvelope> _events = new();
        private int _completed;
        private string _status = "starting";
        private DateTimeOffset? _completedAtUtc;
        private string? _endReason;
        private long _eventCount;
        private string? _provider;
        private string? _model;
        private string? _promptPreview;
        private bool _canResume;
        private string? _error;
        private string? _errorType;
        private int _toolCallCount;
        private int _modelRequestCount;
        private long? _durationMs;
        private DateTimeOffset? _lastEventAtUtc;

        public RunEventHistory(
            string runId,
            Guid sessionId,
            DateTimeOffset startedAtUtc,
            string? provider = null,
            string? model = null,
            string? promptPreview = null,
            bool canResume = false)
        {
            RunId = runId;
            SessionId = sessionId.ToString("D");
            StartedAtUtc = startedAtUtc;
            _provider = provider;
            _model = model;
            _promptPreview = promptPreview;
            _canResume = canResume;
        }

        public static RunEventHistory FromSnapshot(AgentRuntimeRunSnapshot snapshot)
        {
            var history = new RunEventHistory(
                snapshot.RunId,
                Guid.Parse(snapshot.SessionId),
                snapshot.StartedAtUtc,
                snapshot.Provider,
                snapshot.Model,
                snapshot.PromptPreview,
                snapshot.CanResume)
            {
                _completed = 1,
                _status = snapshot.Status,
                _completedAtUtc = snapshot.CompletedAtUtc,
                _endReason = snapshot.EndReason,
                _eventCount = snapshot.EventCount,
                _error = snapshot.Error,
                _errorType = snapshot.ErrorType,
                _toolCallCount = snapshot.ToolCallCount,
                _modelRequestCount = snapshot.ModelRequestCount,
                _durationMs = snapshot.DurationMs,
                _lastEventAtUtc = snapshot.LastEventAtUtc
            };
            return history;
        }

        public string RunId { get; }
        public string SessionId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        public void RecordModelRequest(int requestNumber)
        {
            lock (_gate)
            {
                _modelRequestCount = Math.Max(_modelRequestCount, requestNumber);
                _lastEventAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public void Add(AgentRuntimeStreamEnvelope envelope)
        {
            lock (_gate)
            {
                _events.Enqueue(envelope);
                _eventCount += envelope.Events.Count;
                _lastEventAtUtc = DateTimeOffset.UtcNow;
                foreach (var @event in envelope.Events)
                {
                    if (@event.Type == "run_start")
                    {
                        _status = "running";
                        _provider ??= @event.Provider;
                        _model ??= @event.Model;
                    }
                    else if (@event.Type == "tool_call_update")
                    {
                        _toolCallCount++;
                    }
                    else if (@event.Type == "error")
                    {
                        _error = @event.Message;
                        _errorType = @event.ErrorType;
                    }
                    else if (@event.Type == "loop_end")
                    {
                        _endReason = @event.Reason;
                        _status = @event.Reason switch
                        {
                            "completed" => "completed",
                            "aborted" => "cancelled",
                            "timeout" => "timed_out",
                            "max_iterations" or "limits" or "session_unavailable" or "error" or "provider_error" => "failed",
                            "stopped" => "stopped",
                            _ => "completed"
                        };
                        _completedAtUtc ??= DateTimeOffset.UtcNow;
                        _durationMs ??= Math.Max(
                            0,
                            (long)(_completedAtUtc.Value - StartedAtUtc).TotalMilliseconds);
                    }
                }
                while (_events.Count > MaximumEventsPerRun)
                    _events.Dequeue();
            }
        }

        public void MarkCompleted()
        {
            lock (_gate)
            {
                _completedAtUtc ??= DateTimeOffset.UtcNow;
                if (_status is "starting" or "running")
                {
                    _status = "failed";
                    _endReason ??= "coordinator_closed";
                }
                _durationMs ??= Math.Max(
                    0,
                    (long)(_completedAtUtc.Value - StartedAtUtc).TotalMilliseconds);
                _canResume = false;
            }

            Volatile.Write(ref _completed, 1);
        }

        public void MarkInterrupted()
        {
            lock (_gate)
            {
                _completedAtUtc ??= DateTimeOffset.UtcNow;
                _status = "interrupted";
                _endReason = "application_restart";
                _durationMs ??= Math.Max(
                    0,
                    (long)(_completedAtUtc.Value - StartedAtUtc).TotalMilliseconds);
                _canResume = true;
            }

            Volatile.Write(ref _completed, 1);
        }

        public void DisableResume()
        {
            lock (_gate)
                _canResume = false;
        }

        public AgentRuntimeRunSnapshot ToSnapshot()
        {
            lock (_gate)
            {
                return new AgentRuntimeRunSnapshot(
                    RunId,
                    SessionId,
                    StartedAtUtc,
                    _status,
                    _completedAtUtc,
                    _endReason,
                    _eventCount,
                    _provider,
                    _model,
                    _promptPreview,
                    _error,
                    _errorType,
                    _toolCallCount,
                    _modelRequestCount,
                    _durationMs,
                    _lastEventAtUtc,
                    _canResume);
            }
        }

        public AgentRuntimeRunEventsResult Read(long afterSequence, int limit)
        {
            lock (_gate)
            {
                var retained = _events.ToArray();
                var events = retained
                    .Where(envelope => envelope.Sequence > afterSequence)
                    .Take(limit)
                    .ToArray();
                var nextSequence = events.Length == 0
                    ? afterSequence
                    : events[^1].Sequence;

                return new AgentRuntimeRunEventsResult(
                    RunId,
                    SessionId,
                    events,
                    nextSequence,
                    retained.Any(envelope => envelope.Sequence > nextSequence),
                    retained.Length == 0 ? null : retained[0].Sequence,
                    retained.Length == 0 ? null : retained[^1].Sequence,
                    retained.Length > 0 && afterSequence < retained[0].Sequence - 1,
                    _status,
                    _completedAtUtc,
                    _endReason);
            }
        }
    }

    private sealed class PendingToolApproval
    {
        public PendingToolApproval(string toolCallId, Guid requestId)
        {
            ToolCallId = toolCallId;
            RequestId = requestId;
        }

        public string ToolCallId { get; }
        public Guid RequestId { get; }
        public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ApprovalToken { get; set; }
    }

    private sealed record CredentialRequirement(string Kind, string Prompt);

    private sealed class PendingCredential
    {
        public PendingCredential(
            string requestId,
            string toolCallId,
            string kind,
            string prompt)
        {
            RequestId = requestId;
            ToolCallId = toolCallId;
            Kind = kind;
            Prompt = prompt;
            CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        public string RequestId { get; }
        public string ToolCallId { get; }
        public string Kind { get; }
        public string Prompt { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public TaskCompletionSource<AgentCredentialValue?> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsExpired(DateTimeOffset now)
            => now - CreatedAtUtc > CredentialRequestLifetime;
    }

    private sealed record AgentCredentialValue(string Value, bool RememberForRun);

    private sealed record AgentToolExecutionResult(bool IsSuccess, string Content);

    private sealed class Subscription : IDisposable
    {
        private AgentRunCoordinator? _owner;
        private readonly Action<AgentRuntimeStreamEnvelope> _observer;

        public Subscription(AgentRunCoordinator owner, Action<AgentRuntimeStreamEnvelope> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_observer);
    }
}
