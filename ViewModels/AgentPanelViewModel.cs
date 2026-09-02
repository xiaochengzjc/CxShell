using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtomUI.Controls;
using AtomUI.Controls.Primitives;
using AtomUI.Desktop.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CxShell.Models;
using CxShell.Services;
using CxShell.Services.Agent;
using LiveMarkdown.Avalonia;

namespace CxShell.ViewModels;

/// <summary>
/// Presentation state for the in-process Agent work panel. It intentionally
/// talks to the session gateway and run coordinator only; the view model never
/// receives a terminal control or a raw connection.
/// </summary>
public sealed partial class AgentPanelViewModel : ObservableObject, IDisposable
{
    private const int MaximumPromptCharacters = 32 * 1024;
    private const int MaximumConversationMessages = 40;
    private const int MaximumTranscriptCharacters = 12 * 1024;
    private const int MaximumPendingAttachments = 5;
    private const string SystemPrompt =
        "You are the CxShell operations assistant. Help an operator inspect and troubleshoot " +
        "the selected SSH session. Use session_info for connection context and diagnostic_run " +
        "for fixed read-only checks of the system, disk, network, services, and processes. Use " +
        "runbook_run for SSH or Windows RDP troubleshooting, and fleet_diagnostic for the same " +
        "fixed checks across all currently connected SSH sessions. Use logs_read, port_check, " +
        "service_detail, file_preview, package_query, runtime_check, and disk_cleanup_advice for " +
        "bounded read-only operations. Use session_command only when a " +
        "diagnostic scope cannot answer the question. Explain " +
        "what you are doing, keep commands focused, and never claim a remote change succeeded " +
        "unless the tool result confirms it.";

    private static readonly JsonSerializerOptions RuntimeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAgentRuntimeClient _runtimeClient;
    private readonly IAgentRuntimeStatusSource? _runtimeStatusSource;
    private readonly Func<AgentProviderSettings?> _providerSettings;
    private readonly List<AgentChatMessage> _conversation = [];
    private readonly Dictionary<string, AgentPanelMessageViewModel> _toolMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, SessionAgentState> _sessionStates = new();
    private readonly IDisposable _runtimeSubscription;
    private Dictionary<Guid, AgentSessionSnapshot> _sessionsById = new();
    private AgentPanelMessageViewModel? _currentAssistantMessage;
    private string? _activeRunId;
    private long _lastRunSequence;
    private bool _isRecoveringRun;
    private bool _runRecoveryRequested;
    private long _sessionRefreshVersion;
    private long _runHistoryRefreshVersion;
    private Guid? _preferredSessionId;
    private Guid? _conversationSessionId;
    private ISelectOption? _lastSelectedSessionOption;
    private readonly Dictionary<string, string> _runPrompts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _backgroundRunControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentPanelStepViewModel> _activeRunStepMap = new(StringComparer.Ordinal);
    private DispatcherTimer? _runElapsedTimer;
    private DateTimeOffset? _runStartedAtUtc;
    private string _activeRunSessionName = string.Empty;
    private int _activeRunToolCallCount;
    private int _activeRunModelRequestCount;
    private int _disposeState;

    [ObservableProperty] private ISelectOption? _selectedSessionOption;
    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStopping;
    [ObservableProperty] private bool _isCanceling;
    [ObservableProperty] private bool _isAppending;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _providerStatusText = string.Empty;
    [ObservableProperty] private bool _isProviderReady;
    [ObservableProperty] private bool _isTestingProvider;
    [ObservableProperty] private string _providerTestStatusText = string.Empty;
    [ObservableProperty] private string _runtimeStatusText = string.Empty;
    [ObservableProperty] private string _runtimeStatusDetails = string.Empty;
    [ObservableProperty] private bool _isRuntimeReady;
    [ObservableProperty] private AgentRuntimeSessionState _runtimeState = AgentRuntimeSessionState.NotInitialized;
    [ObservableProperty] private string _runtimeErrorText = string.Empty;
    [ObservableProperty] private bool _isHistoryVisible;
    [ObservableProperty] private AgentPanelRunViewModel? _selectedRun;
    [ObservableProperty] private string _runHistorySearch = string.Empty;
    [ObservableProperty] private ISelectOption? _selectedRunHistoryFilterOption;
    [ObservableProperty] private int _activeRunCount;
    [ObservableProperty] private string _runElapsedText = string.Empty;
    [ObservableProperty] private AgentRunCheckpoint? _activeRunCheckpoint;
    [ObservableProperty] private string _activeRunPhaseText = string.Empty;
    [ObservableProperty] private string _activeRunCheckpointText = string.Empty;
    [ObservableProperty] private bool _hasActiveRunCheckpoint;

    public ObservableCollection<ISelectOption> SessionOptions { get; } = new();
    public ObservableCollection<AgentPanelMessageViewModel> Messages { get; } = new();
    public ObservableCollection<AgentAttachmentViewModel> PendingAttachments { get; } = new();
    public ObservableCollection<AgentPanelRunViewModel> RunHistory { get; } = new();
    public ObservableCollection<AgentPanelRunViewModel> FilteredRunHistory { get; } = new();
    public ObservableCollection<ISelectOption> RunHistoryFilterOptions { get; } = new();
    public ObservableCollection<AgentPanelStepViewModel> ActiveRunSteps { get; } = new();

    public AgentPanelViewModel(
        IAgentRuntimeClient runtimeClient,
        Func<AgentProviderSettings?>? providerSettings = null)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _runtimeStatusSource = runtimeClient as IAgentRuntimeStatusSource;
        _providerSettings = providerSettings ?? (() => null);
        RebuildRunHistoryFilterOptions();
        _runtimeSubscription = _runtimeClient.SubscribeEvents(OnRuntimeEvent);
        if (_runtimeStatusSource != null)
        {
            _runtimeStatusSource.StatusChanged += OnRuntimeStatusChanged;
            ApplyRuntimeStatus(_runtimeStatusSource.Status);
        }
        else
        {
            RuntimeStatusText = RuntimeNotInitializedText;
        }
        RefreshProviderStatus();
    }

    public AgentSessionSnapshot? SelectedSession
        => SelectedSessionId is { } sessionId && _sessionsById.TryGetValue(sessionId, out var session)
            ? session
            : null;

    public Guid? SelectedSessionId
        => Guid.TryParse(SelectedSessionOption?.Content?.ToString(), out var sessionId) && sessionId != Guid.Empty
            ? sessionId
            : null;

    public string TitleText => Text("Agent.Title");
    public string DescriptionText => Text("Agent.Description");
    public string SessionText => Text("Agent.Session");
    public string PromptText => Text("Agent.Prompt");
    public string PromptPlaceholderText => Text("Agent.PromptPlaceholder");
    public string AttachFileText => Text("Agent.AttachFile");
    public string AttachFileTipText => Text("Agent.AttachFileTip");
    public string RemoveAttachmentText => Text("Agent.RemoveAttachment");
    public string RunText => Text("Agent.Run");
    public string AppendText => Text("Agent.Append");
    public string StopText => Text("Agent.Stop");
    public string CancelText => Text("Agent.Cancel");
    public string StoppingText => Text("Agent.Stopping");
    public string CancellingText => Text("Agent.Cancelling");
    public string FollowUpQueuedText => Text("Agent.FollowUpQueued");
    public string StoppedText => Text("Agent.Stopped");
    public string RefreshText => Text("Agent.Refresh");
    public string CloseText => Text("Agent.Close");
    public string EmptySessionsText => Text("Agent.EmptySessions");
    public string NoSessionText => Text("Agent.NoSession");
    public string ConnectedText => Text("Agent.Connected");
    public string DisconnectedText => Text("Agent.Disconnected");
    public string ProviderReadyText => Text("Agent.ProviderReady");
    public string ProviderUnavailableText => Text("Agent.ProviderUnavailable");
    public string ProviderTestText => Text("Agent.ProviderTest");
    public string ProviderTestingText => Text("Agent.ProviderTesting");
    public string ProviderTestSucceededText => Text("Agent.ProviderTestSucceeded");
    public string ProviderTestFailedText => Text("Agent.ProviderTestFailed");
    public string RuntimeReadyText => Text("Agent.RuntimeReady");
    public string RuntimeUnavailableText => Text("Agent.RuntimeUnavailable");
    public string RuntimeNotInitializedText => Text("Agent.RuntimeNotInitialized");
    public string RuntimeInitializingText => Text("Agent.RuntimeInitializing");
    public string RuntimeRetryText => Text("Agent.RuntimeRetry");
    public string SessionRefreshFailedText => Text("Agent.SessionRefreshFailed");
    public string RunningText => Text("Agent.Running");
    public string ReadyText => Text("Agent.Ready");
    public string CompletedText => Text("Agent.Completed");
    public string CancelledText => Text("Agent.Cancelled");
    public string ErrorText => Text("Agent.Error");
    public string UserMessageText => Text("Agent.UserMessage");
    public string AssistantMessageText => Text("Agent.AssistantMessage");
    public string ToolMessageText => Text("Agent.ToolMessage");
    public string ApprovalRequiredText => Text("Agent.ApprovalRequired");
    public string CredentialRequiredText => Text("Agent.CredentialRequired");
    public string CredentialPlaceholderText => Text("Agent.CredentialPlaceholder");
    public string RememberCredentialText => Text("Agent.RememberCredential");
    public string SubmitCredentialText => Text("Agent.SubmitCredential");
    public string ApproveText => Text("Agent.Approve");
    public string DenyText => Text("Agent.Deny");
    public string ApprovalDeniedText => Text("Agent.ApprovalDenied");
    public string HistoryText => Text("Agent.History");
    public string HistoryClearText => Text("Agent.HistoryClear");
    public string HistoryEmptyText => Text("Agent.HistoryEmpty");
    public string HistoryDetailsText => Text("Agent.HistoryDetails");
    public string HistoryRetryText => Text("Agent.HistoryRetry");
    public string HistoryContinueText => Text("Agent.HistoryContinue");
    public string HistoryLoadingText => Text("Agent.HistoryLoading");
    public string HistoryClearedText => Text("Agent.HistoryCleared");
    public string HistoryFilterText => Text("Agent.HistoryFilter");
    public string HistoryFilterAllText => Text("Agent.HistoryFilterAll");
    public string HistoryFilterCurrentSessionText => Text("Agent.HistoryFilterCurrentSession");
    public string HistoryFilterRunningText => Text("Agent.HistoryFilterRunning");
    public string HistoryFilterWaitingText => Text("Agent.HistoryFilterWaiting");
    public string HistoryFilterCompletedText => Text("Agent.HistoryFilterCompleted");
    public string HistoryFilterFailedText => Text("Agent.HistoryFilterFailed");
    public string HistorySearchText => Text("Agent.HistorySearch");
    public string HistoryFilterEmptyText => Text("Agent.HistoryFilterEmpty");
    public string HistoryFocusText => Text("Agent.HistoryFocus");
    public string HistoryStopText => Text("Agent.HistoryStop");
    public string HistoryCancelText => Text("Agent.HistoryCancel");
    public string ActiveRunsText => Text("Agent.ActiveRuns");
    public string NoActiveRunsText => Text("Agent.NoActiveRuns");
    public string WaitingForInputText => Text("Agent.WaitingForInput");
    public string PendingApprovalText => Text("Agent.PendingApproval");
    public string RunElapsedLabelText => Text("Agent.RunElapsed");
    public string ScrollToLatestText => Text("Agent.ScrollToLatest");
    public string HistoryEmptyDisplayText => HasRunHistory
        ? HistoryFilterEmptyText
        : HistoryEmptyText;

    public bool HasSessions => SessionOptions.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    public bool HasPendingAttachments => PendingAttachments.Count > 0;
    public bool HasRunHistory => RunHistory.Count > 0;
    public bool HasFilteredRunHistory => FilteredRunHistory.Count > 0;
    public bool HasActiveRuns => ActiveRunCount > 0;
    public bool HasActiveRunSteps => ActiveRunSteps.Count > 0;
    public bool HasProviderTestStatus => !string.IsNullOrWhiteSpace(ProviderTestStatusText);
    public bool IsRunElapsedVisible => IsRunning && _runStartedAtUtc.HasValue;
    public string ActivityStatusText => HasActiveRuns
        ? string.Format(ActiveRunsText, ActiveRunCount)
        : NoActiveRunsText;
    public bool HasSelectedSession => SelectedSession != null;
    public bool IsSelectedSessionConnected => SelectedSession?.IsConnected == true;
    public bool IsSessionSelectionEnabled => HasSessions && !IsRunning;
    public bool IsRuntimeRetryVisible => RuntimeState == AgentRuntimeSessionState.Failed && !IsRunning;
    public bool IsPromptInputEnabled => !IsStopping && !IsCanceling;
    public string SelectedSessionStatusText => SelectedSession switch
    {
        { IsConnected: true } => ConnectedText,
        { IsConnected: false } => DisconnectedText,
        _ => NoSessionText
    };

    public bool CanRun()
        => !IsRunning &&
           IsRuntimeReady &&
           IsProviderReady &&
           IsSelectedSessionConnected &&
           (!string.IsNullOrWhiteSpace(Prompt) || HasPendingAttachments);

    public bool CanAppend()
        => IsRunning &&
           !IsStopping &&
           !IsCanceling &&
           !IsAppending &&
           !string.IsNullOrWhiteSpace(_activeRunId) &&
           (!string.IsNullOrWhiteSpace(Prompt) || HasPendingAttachments);

    public bool CanStop()
        => IsRunning &&
           !IsStopping &&
           !IsCanceling &&
           !string.IsNullOrWhiteSpace(_activeRunId);

    public bool CanCancel()
        => IsRunning &&
           !IsCanceling &&
           !string.IsNullOrWhiteSpace(_activeRunId);

    public bool CanRetryRuntime()
        => RuntimeState == AgentRuntimeSessionState.Failed && !IsRunning;

    public bool CanTestProvider()
        => IsRuntimeReady && !IsTestingProvider;

    private void NotifyRunCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        AppendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        FocusRunCommand.NotifyCanExecuteChanged();
        StopBackgroundRunCommand.NotifyCanExecuteChanged();
        CancelBackgroundRunCommand.NotifyCanExecuteChanged();
        TestProviderCommand.NotifyCanExecuteChanged();
    }

    public bool CanFocusRun(AgentPanelRunViewModel? run)
        => run?.IsActive == true &&
           !IsRunning &&
           !_backgroundRunControls.Contains(run.RunId);

    public bool CanManageBackgroundRun(AgentPanelRunViewModel? run)
        => run?.IsActive == true &&
           !IsRunning &&
           !_backgroundRunControls.Contains(run.RunId);

    private void BeginRuntimeRefresh()
    {
        if (_runtimeStatusSource?.Status.State == AgentRuntimeSessionState.Ready)
            return;

        RuntimeState = AgentRuntimeSessionState.Initializing;
        IsRuntimeReady = false;
        RuntimeErrorText = string.Empty;
        RuntimeStatusText = RuntimeInitializingText;
        RuntimeStatusDetails = RuntimeStatusText;
    }

    private void OnRuntimeStatusChanged(AgentRuntimeSessionStatus status)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRuntimeStatusChanged(status));
            return;
        }

        ApplyRuntimeStatus(status);
        NotifyRunCommands();
        RetryRuntimeCommand.NotifyCanExecuteChanged();
    }

    private void ApplyRuntimeStatus(AgentRuntimeSessionStatus status)
    {
        RuntimeState = status.State;
        IsRuntimeReady = status.State == AgentRuntimeSessionState.Ready;
        RuntimeErrorText = status.Error ?? string.Empty;
        RuntimeStatusText = status.State switch
        {
            AgentRuntimeSessionState.NotInitialized => RuntimeNotInitializedText,
            AgentRuntimeSessionState.Initializing => RuntimeInitializingText,
            AgentRuntimeSessionState.Ready => RuntimeReadyText,
            AgentRuntimeSessionState.Failed => BuildRuntimeFailureText(status),
            AgentRuntimeSessionState.Disposed => RuntimeUnavailableText,
            _ => RuntimeUnavailableText
        };
        RuntimeStatusDetails = BuildRuntimeStatusDetails(status);
    }

    private string BuildRuntimeFailureText(AgentRuntimeSessionStatus status)
        => string.IsNullOrWhiteSpace(status.Error)
            ? RuntimeUnavailableText
            : $"{RuntimeUnavailableText}: {status.Error}";

    private void ApplyRuntimeReadyStatus()
    {
        RuntimeState = AgentRuntimeSessionState.Ready;
        IsRuntimeReady = true;
        RuntimeErrorText = string.Empty;
        RuntimeStatusText = RuntimeReadyText;
        RuntimeStatusDetails = RuntimeStatusText;
    }

    private void ApplySessionRefreshFailure(Exception exception)
    {
        var status = _runtimeStatusSource?.Status;
        if (status?.State == AgentRuntimeSessionState.Ready)
        {
            // The Runtime handshake succeeded; only the gateway request failed.
            // Keep Run available and report the narrower failure accurately.
            RuntimeState = AgentRuntimeSessionState.Ready;
            IsRuntimeReady = true;
            RuntimeErrorText = exception.Message;
            RuntimeStatusText = $"{RuntimeReadyText} - {SessionRefreshFailedText}: {exception.Message}";
            RuntimeStatusDetails = RuntimeStatusText;
            return;
        }

        if (status != null)
        {
            ApplyRuntimeStatus(status);
            return;
        }

        var failedStatus = new AgentRuntimeSessionStatus(
            AgentRuntimeSessionState.Failed,
            0,
            null,
            exception is AgentRuntimeRequestException requestException
                ? requestException.Response.ErrorCode
                : null,
            exception.Message,
            DateTimeOffset.UtcNow);
        ApplyRuntimeStatus(failedStatus);
    }

    private string BuildRuntimeStatusDetails(AgentRuntimeSessionStatus status)
    {
        var details = new List<string> { RuntimeStatusText };
        if (status.InitializationAttempt > 0)
            details.Add($"{Text("Agent.RuntimeAttempt")}: {status.InitializationAttempt}");
        if (!string.IsNullOrWhiteSpace(status.ErrorCode))
            details.Add($"{Text("Agent.RuntimeErrorCode")}: {status.ErrorCode}");
        if (!string.IsNullOrWhiteSpace(status.RequestId))
            details.Add($"{Text("Agent.RuntimeRequestId")}: {status.RequestId}");
        return string.Join(Environment.NewLine, details);
    }

    public void RefreshSessions()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshSessions);
            return;
        }

        var selectedId = SelectedSessionId ?? _preferredSessionId;
        var version = Interlocked.Increment(ref _sessionRefreshVersion);
        BeginRuntimeRefresh();
        _ = RefreshSessionsAsync(version, selectedId);
    }

    private async Task RefreshSessionsAsync(long version, Guid? selectedId)
    {
        AgentRuntimeSessionListResult result;
        try
        {
            result = await _runtimeClient.SendResultAsync<AgentRuntimeSessionListResult>(
                    AgentRuntimeMethodNames.SessionList)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _sessionRefreshVersion) ||
                    Volatile.Read(ref _disposeState) != 0)
                {
                    return;
                }

                ApplySessionRefreshFailure(exception);
                NotifyRunCommands();
                RetryRuntimeCommand.NotifyCanExecuteChanged();
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _sessionRefreshVersion) ||
                Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_runtimeStatusSource == null)
                ApplyRuntimeReadyStatus();
            else
                ApplyRuntimeStatus(_runtimeStatusSource.Status);
            var snapshots = result.Sessions
                .Where(session => session.Protocol == SessionProtocol.SSH)
                .OrderByDescending(session => session.IsConnected)
                .ThenBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.Host, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ApplySessionSnapshots(snapshots, selectedId);
        });
    }

    private void ApplySessionSnapshots(
        IReadOnlyList<AgentSessionSnapshot> snapshots,
        Guid? selectedId)
    {
        _sessionsById = snapshots.ToDictionary(session => session.SessionId);
        SessionOptions.Clear();
        foreach (var session in snapshots)
        {
            SessionOptions.Add(new SelectOption
            {
                Header = BuildSessionHeader(session),
                Content = session.SessionId.ToString("D")
            });
        }

        var option = selectedId is { } id
            ? SessionOptions.FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedSessionOption = option;

        // The selection callback captures the current transcript. Prune closed
        // session state only after that callback has finished.
        var currentSessionIds = _sessionsById.Keys.ToHashSet();
        foreach (var sessionId in _sessionStates.Keys
                     .Where(id => !currentSessionIds.Contains(id))
                     .ToArray())
        {
            _sessionStates.Remove(sessionId);
        }

        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(IsSelectedSessionConnected));
        OnPropertyChanged(nameof(SelectedSessionStatusText));
        OnPropertyChanged(nameof(IsSessionSelectionEnabled));
        NotifyRunCommands();
    }

    public void EnsureSessionSelection(Guid? preferredSessionId = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => EnsureSessionSelection(preferredSessionId));
            return;
        }

        if (SelectedSession != null)
            return;

        _preferredSessionId = preferredSessionId;

        ISelectOption? option = null;
        if (preferredSessionId is { } preferred && preferred != Guid.Empty)
        {
            option = SessionOptions.FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), preferred.ToString("D"), StringComparison.OrdinalIgnoreCase));
        }

        option ??= SessionOptions.FirstOrDefault(item =>
            Guid.TryParse(item.Content?.ToString(), out var id) &&
            _sessionsById.TryGetValue(id, out var session) &&
            session.IsConnected);
        option ??= SessionOptions.FirstOrDefault();
        if (option != null)
            SelectedSessionOption = option;
    }

    public void RefreshProviderStatus()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshProviderStatus);
            return;
        }

        var validation = AgentProviderConfiguration.Validate(_providerSettings());
        IsProviderReady = validation.IsValid;
        ProviderStatusText = validation.IsValid ? ProviderReadyText : ProviderUnavailableText;
        NotifyRunCommands();
    }

    public void RefreshActiveRun()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshActiveRun);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeRunId))
        {
            StartRunRecovery(_activeRunId, _lastRunSequence);
            return;
        }

        // The panel can be hidden while a Runtime run continues in the
        // coordinator. Refreshing the run list lets this panel adopt that run
        // before it starts a second one for the same SSH session.
        RefreshRunHistory();
    }

    public void RefreshRunHistory()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshRunHistory);
            return;
        }

        var version = Interlocked.Increment(ref _runHistoryRefreshVersion);
        _ = RefreshRunHistoryAsync(version);
    }

    private async Task RefreshRunHistoryAsync(long version)
    {
        AgentRuntimeRunListResult result;
        try
        {
            result = await _runtimeClient.SendResultAsync<AgentRuntimeRunListResult>(
                    AgentRuntimeMethodNames.RunList,
                    new { limit = AgentRunCoordinator.MaximumRetainedRuns },
                    requestId: $"agent-panel-history-{Guid.NewGuid():N}")
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != Volatile.Read(ref _runHistoryRefreshVersion) ||
                Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            var expandedRunIds = RunHistory
                .Where(run => run.IsExpanded)
                .Select(run => run.RunId)
                .ToHashSet(StringComparer.Ordinal);
            RunHistory.Clear();
            var listedRunIds = result.Runs
                .Select(snapshot => snapshot.RunId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var snapshot in result.Runs)
            {
                var run = new AgentPanelRunViewModel(
                    snapshot,
                    _runPrompts.ContainsKey(snapshot.RunId),
                    snapshot.CanResume,
                    GetRunSessionLabel(snapshot.SessionId));
                run.IsExpanded = expandedRunIds.Contains(run.RunId);
                RunHistory.Add(run);
            }

            foreach (var snapshot in result.Runs)
            {
                if (IsActiveRun(snapshot))
                {
                    if (!_endedRunIds.Contains(snapshot.RunId))
                        _activeRunIds.Add(snapshot.RunId);
                }
                else
                {
                    _activeRunIds.Remove(snapshot.RunId);
                    _endedRunIds.Add(snapshot.RunId);
                }
            }
            ApplyRunSnapshotsToSummaries(result.Runs);
            foreach (var runId in _endedRunIds.Where(runId => !listedRunIds.Contains(runId)).ToArray())
                _endedRunIds.Remove(runId);
            ActiveRunCount = _activeRunIds.Count;
            RefreshFilteredRunHistory();
            NotifyRunCommands();

            SelectedRun = SelectedRun == null
                ? null
                : RunHistory.FirstOrDefault(run => run.RunId == SelectedRun.RunId);
            OnPropertyChanged(nameof(HasRunHistory));
            RestoreActiveRun(result.Runs);
        });
    }

    private void RestoreActiveRun(IReadOnlyList<AgentRuntimeRunSnapshot> runs)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!string.IsNullOrWhiteSpace(_activeRunId))
        {
            // The panel may have missed the terminal event while it was hidden.
            // Replay the retained tail and let loop_end settle the state.
            var currentRun = runs.FirstOrDefault(run =>
                string.Equals(run.RunId, _activeRunId, StringComparison.Ordinal));
            if (currentRun != null)
            {
                ApplyRunCheckpoint(currentRun.Checkpoint);
                RestoreRunSteps(currentRun.Steps);
                StartRunRecovery(_activeRunId, _lastRunSequence);
            }
            return;
        }

        var selectedSessionId = SelectedSessionId ?? _preferredSessionId;
        if (selectedSessionId is not { } sessionId || sessionId == Guid.Empty)
            return;

        var activeRun = FindActiveRun(runs, sessionId);
        if (activeRun == null)
            return;

        AttachActiveRun(activeRun);
    }

    private void AttachActiveRun(AgentRuntimeRunSnapshot activeRun)
    {
        if (IsRunning || !AgentRunStates.IsActive(activeRun.Status))
            return;

        _activeRunId = activeRun.RunId;
        _lastRunSequence = 0;
        _isRecoveringRun = false;
        _runRecoveryRequested = false;
        _currentAssistantMessage = null;
        _toolMessages.Clear();
        RestoreRunSteps(activeRun.Steps);
        IsStopping = false;
        IsCanceling = false;
        IsAppending = false;
        IsRunning = true;
        StatusText = RunningText;
        ApplyRunCheckpoint(activeRun.Checkpoint);
        StartRunTracking(
            activeRun.StartedAtUtc,
            activeRun.ModelRequestCount,
            activeRun.ToolCallCount,
            SelectedSession?.Name ?? string.Empty);
        NotifyRunCommands();
        StartRunRecovery(activeRun.RunId, 0);
    }

    private void RebuildRunHistoryFilterOptions()
    {
        var selectedValue = SelectedRunHistoryFilterOption?.Content?.ToString() ?? "all";
        RunHistoryFilterOptions.Clear();
        RunHistoryFilterOptions.Add(new SelectOption
        {
            Header = HistoryFilterAllText,
            Content = "all"
        });
        RunHistoryFilterOptions.Add(new SelectOption
        {
            Header = HistoryFilterCurrentSessionText,
            Content = "current"
        });
        RunHistoryFilterOptions.Add(new SelectOption
        {
            Header = HistoryFilterRunningText,
            Content = "running"
        });
        RunHistoryFilterOptions.Add(new SelectOption
        {
            Header = HistoryFilterWaitingText,
            Content = "waiting"
        });
        RunHistoryFilterOptions.Add(new SelectOption
        {
            Header = HistoryFilterCompletedText,
            Content = "completed"
        });
        RunHistoryFilterOptions.Add(new SelectOption
        {
            Header = HistoryFilterFailedText,
            Content = "failed"
        });
        SelectedRunHistoryFilterOption = RunHistoryFilterOptions.FirstOrDefault(option =>
            string.Equals(option.Content?.ToString(), selectedValue, StringComparison.Ordinal))
            ?? RunHistoryFilterOptions[0];
    }

    private void RefreshFilteredRunHistory()
    {
        var filter = SelectedRunHistoryFilterOption?.Content?.ToString() ?? "all";
        var selectedSessionId = SelectedSessionId;
        var search = RunHistorySearch.Trim();
        var filtered = RunHistory
            .Where(run => filter switch
            {
                "current" => selectedSessionId is { } sessionId &&
                              string.Equals(run.SessionId, sessionId.ToString("D"), StringComparison.OrdinalIgnoreCase),
                "running" => run.IsActive && !run.IsWaiting,
                "waiting" => run.IsWaiting,
                "completed" => string.Equals(run.Status, AgentRunStates.Completed, StringComparison.OrdinalIgnoreCase),
                "failed" => run.IsFailure,
                _ => true
            })
            .Where(run => run.MatchesSearch(search))
            .ToArray();

        FilteredRunHistory.Clear();
        foreach (var run in filtered)
            FilteredRunHistory.Add(run);
        OnPropertyChanged(nameof(HasFilteredRunHistory));
        OnPropertyChanged(nameof(HistoryEmptyDisplayText));
        NotifyRunCommands();
    }

    private void UpdateRunActivity(AgentRuntimeStreamEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.RunId))
            return;

        var hasRunStart = envelope.Events.Any(@event =>
            string.Equals(@event.Type, "run_start", StringComparison.Ordinal));
        if (hasRunStart)
            _endedRunIds.Remove(envelope.RunId);
        if (!_endedRunIds.Contains(envelope.RunId))
            _activeRunIds.Add(envelope.RunId);
        var runEnded = false;
        foreach (var @event in envelope.Events)
        {
            if (string.Equals(@event.Type, "loop_end", StringComparison.Ordinal))
            {
                _activeRunIds.Remove(envelope.RunId);
                _endedRunIds.Add(envelope.RunId);
                runEnded = true;
            }
        }

        ActiveRunCount = _activeRunIds.Count;
        NotifyRunCommands();
        if (runEnded && !string.Equals(_activeRunId, envelope.RunId, StringComparison.Ordinal))
            RefreshRunHistory();
    }

    private void ApplyRunSnapshotsToSummaries(IReadOnlyList<AgentRuntimeRunSnapshot> snapshots)
    {
        foreach (var summary in Messages.Where(message => message.IsSummary))
        {
            var snapshot = snapshots.FirstOrDefault(item =>
                string.Equals(item.RunId, summary.RunId, StringComparison.Ordinal));
            if (snapshot != null)
                summary.UpdateSummaryMetrics(snapshot);
        }
    }

    private void ApplyRunCheckpoint(AgentRunCheckpoint? checkpoint)
    {
        ActiveRunCheckpoint = checkpoint;
        HasActiveRunCheckpoint = checkpoint != null;
        if (checkpoint != null)
        {
            _activeRunModelRequestCount = Math.Max(
                _activeRunModelRequestCount,
                checkpoint.ModelRequestCount);
            _activeRunToolCallCount = Math.Max(
                _activeRunToolCallCount,
                checkpoint.ToolCallCount);
        }
        ActiveRunPhaseText = checkpoint == null
            ? string.Empty
            : AgentCheckpointDisplay.PhaseText(checkpoint);
        ActiveRunCheckpointText = checkpoint == null
            ? string.Empty
            : AgentCheckpointDisplay.ProgressText(checkpoint);
    }

    private void StartRunTracking(
        DateTimeOffset startedAtUtc,
        int modelRequestCount,
        int toolCallCount,
        string sessionName)
    {
        _runElapsedTimer?.Stop();
        _runElapsedTimer = null;
        _runStartedAtUtc = startedAtUtc;
        _activeRunSessionName = sessionName;
        _activeRunToolCallCount = Math.Max(0, toolCallCount);
        _activeRunModelRequestCount = Math.Max(0, modelRequestCount);
        RunElapsedText = FormatDuration(DateTimeOffset.UtcNow - startedAtUtc);
        OnPropertyChanged(nameof(IsRunElapsedVisible));

        _runElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runElapsedTimer.Tick += OnRunElapsedTimerTick;
        _runElapsedTimer.Start();
    }

    private void OnRunElapsedTimerTick(object? sender, EventArgs e)
    {
        if (_runStartedAtUtc is { } startedAtUtc)
            RunElapsedText = FormatDuration(DateTimeOffset.UtcNow - startedAtUtc);
    }

    private string StopRunTracking()
    {
        if (_runStartedAtUtc is not { } startedAtUtc)
            return string.Empty;

        RunElapsedText = FormatDuration(DateTimeOffset.UtcNow - startedAtUtc);
        var durationText = RunElapsedText;
        if (_runElapsedTimer != null)
        {
            _runElapsedTimer.Tick -= OnRunElapsedTimerTick;
            _runElapsedTimer.Stop();
            _runElapsedTimer = null;
        }

        _runStartedAtUtc = null;
        OnPropertyChanged(nameof(IsRunElapsedVisible));
        return durationText;
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalHours = (int)duration.TotalHours;
        return totalHours > 0
            ? $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static bool IsActiveRun(AgentRuntimeRunSnapshot run)
        => AgentRunStates.IsActive(run.Status);

    internal static AgentRuntimeRunSnapshot? FindActiveRun(
        IReadOnlyList<AgentRuntimeRunSnapshot> runs,
        Guid sessionId)
        => runs
            .Where(run =>
                string.Equals(run.SessionId, sessionId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                AgentRunStates.IsActive(run.Status))
            .OrderByDescending(run => run.StartedAtUtc)
            .FirstOrDefault();

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(SessionText));
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(PromptPlaceholderText));
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(AppendText));
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(StoppingText));
        OnPropertyChanged(nameof(CancellingText));
        OnPropertyChanged(nameof(FollowUpQueuedText));
        OnPropertyChanged(nameof(StoppedText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(EmptySessionsText));
        OnPropertyChanged(nameof(NoSessionText));
        OnPropertyChanged(nameof(ConnectedText));
        OnPropertyChanged(nameof(DisconnectedText));
        OnPropertyChanged(nameof(ProviderReadyText));
        OnPropertyChanged(nameof(ProviderUnavailableText));
        OnPropertyChanged(nameof(ProviderTestText));
        OnPropertyChanged(nameof(ProviderTestingText));
        OnPropertyChanged(nameof(ProviderTestSucceededText));
        OnPropertyChanged(nameof(ProviderTestFailedText));
        OnPropertyChanged(nameof(RuntimeReadyText));
        OnPropertyChanged(nameof(RuntimeUnavailableText));
        OnPropertyChanged(nameof(RuntimeStatusDetails));
        OnPropertyChanged(nameof(RuntimeNotInitializedText));
        OnPropertyChanged(nameof(RuntimeInitializingText));
        OnPropertyChanged(nameof(RuntimeRetryText));
        OnPropertyChanged(nameof(SessionRefreshFailedText));
        OnPropertyChanged(nameof(RunningText));
        OnPropertyChanged(nameof(ReadyText));
        OnPropertyChanged(nameof(CompletedText));
        OnPropertyChanged(nameof(CancelledText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(UserMessageText));
        OnPropertyChanged(nameof(AssistantMessageText));
        OnPropertyChanged(nameof(ToolMessageText));
        OnPropertyChanged(nameof(ApprovalRequiredText));
        OnPropertyChanged(nameof(ApproveText));
        OnPropertyChanged(nameof(DenyText));
        OnPropertyChanged(nameof(ApprovalDeniedText));
        OnPropertyChanged(nameof(HistoryText));
        OnPropertyChanged(nameof(HistoryClearText));
        OnPropertyChanged(nameof(HistoryEmptyText));
        OnPropertyChanged(nameof(HistoryDetailsText));
        OnPropertyChanged(nameof(HistoryRetryText));
        OnPropertyChanged(nameof(HistoryContinueText));
        OnPropertyChanged(nameof(HistoryLoadingText));
        OnPropertyChanged(nameof(HistoryClearedText));
        OnPropertyChanged(nameof(HistoryFilterText));
        OnPropertyChanged(nameof(HistoryFilterAllText));
        OnPropertyChanged(nameof(HistoryFilterCurrentSessionText));
        OnPropertyChanged(nameof(HistoryFilterRunningText));
        OnPropertyChanged(nameof(HistoryFilterWaitingText));
        OnPropertyChanged(nameof(HistoryFilterCompletedText));
        OnPropertyChanged(nameof(HistoryFilterFailedText));
        OnPropertyChanged(nameof(HistorySearchText));
        OnPropertyChanged(nameof(HistoryFilterEmptyText));
        OnPropertyChanged(nameof(HistoryFocusText));
        OnPropertyChanged(nameof(HistoryStopText));
        OnPropertyChanged(nameof(HistoryCancelText));
        OnPropertyChanged(nameof(ActiveRunsText));
        OnPropertyChanged(nameof(NoActiveRunsText));
        OnPropertyChanged(nameof(WaitingForInputText));
        OnPropertyChanged(nameof(PendingApprovalText));
        OnPropertyChanged(nameof(RunElapsedLabelText));
        OnPropertyChanged(nameof(RunElapsedText));
        OnPropertyChanged(nameof(ScrollToLatestText));
        OnPropertyChanged(nameof(IsRunElapsedVisible));
        OnPropertyChanged(nameof(ActivityStatusText));
        ApplyRunCheckpoint(ActiveRunCheckpoint);
        OnPropertyChanged(nameof(HistoryEmptyDisplayText));
        RebuildRunHistoryFilterOptions();
        RefreshFilteredRunHistory();
        OnPropertyChanged(nameof(SelectedSessionStatusText));
        foreach (var message in Messages)
            message.NotifyLocalizationChanged();
        RefreshProviderStatus();
        foreach (var run in RunHistory)
            run.NotifyLocalizationChanged();
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshSessions();
        RefreshProviderStatus();
        RefreshActiveRun();
        RefreshRunHistory();
    }

    [RelayCommand(CanExecute = nameof(CanTestProvider))]
    private async Task TestProvider()
    {
        if (!CanTestProvider())
            return;

        IsTestingProvider = true;
        ProviderTestStatusText = ProviderTestingText;
        NotifyRunCommands();
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeProviderTestResult>(
                    AgentRuntimeMethodNames.ProviderTest,
                    requestId: $"agent-panel-provider-test-{Guid.NewGuid():N}")
                .ConfigureAwait(true);

            ProviderTestStatusText = result.Reachable
                ? $"{ProviderTestSucceededText} ({result.DurationMs} ms)"
                : $"{ProviderTestFailedText}: {result.Message}";
        }
        catch (Exception exception)
        {
            ProviderTestStatusText = $"{ProviderTestFailedText}: {exception.Message}";
        }
        finally
        {
            IsTestingProvider = false;
            NotifyRunCommands();
        }
    }

    [RelayCommand]
    private void ToggleRunHistory()
    {
        IsHistoryVisible = !IsHistoryVisible;
        if (IsHistoryVisible)
            RefreshRunHistory();
    }

    [RelayCommand]
    private async Task ClearRunHistory()
    {
        try
        {
            await _runtimeClient.SendResultAsync<AgentRuntimeRunClearResult>(
                    AgentRuntimeMethodNames.RunClear,
                    requestId: $"agent-panel-history-clear-{Guid.NewGuid():N}")
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RunHistory.Clear();
                _endedRunIds.Clear();
                RefreshFilteredRunHistory();
                SelectedRun = null;
                OnPropertyChanged(nameof(HasRunHistory));
                StatusText = HistoryClearedText;
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = exception.Message);
        }
    }

    [RelayCommand]
    private async Task ToggleRunDetails(AgentPanelRunViewModel? run)
    {
        if (run == null)
            return;

        SelectedRun = run;
        run.IsExpanded = !run.IsExpanded;
        if (!run.IsExpanded || run.HasLoadedDetails || run.IsLoadingDetails)
            return;

        run.IsLoadingDetails = true;
        run.DetailsText = HistoryLoadingText;
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunEventsResult>(
                    AgentRuntimeMethodNames.RunEvents,
                    new
                    {
                        runId = run.RunId,
                        limit = AgentRunCoordinator.MaximumEventReadLimit
                    },
                    requestId: $"agent-panel-history-details-{Guid.NewGuid():N}")
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                run.DetailsText = BuildRunDetails(result);
                run.HasLoadedDetails = true;
                run.IsLoadingDetails = false;
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                run.DetailsText = exception.Message;
                run.IsLoadingDetails = false;
            });
        }
    }

    [RelayCommand]
    private void ToggleToolDetails(AgentPanelMessageViewModel? message)
    {
        if (message?.IsTool == true)
            message.IsToolDetailsExpanded = !message.IsToolDetailsExpanded;
    }

    [RelayCommand]
    private async Task RetryRun(AgentPanelRunViewModel? run)
    {
        if (run == null || IsRunning || !_runPrompts.TryGetValue(run.RunId, out var prompt))
            return;

        var option = SessionOptions.FirstOrDefault(item =>
            string.Equals(
                item.Content?.ToString(),
                run.SessionId,
                StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            StatusText = DisconnectedText;
            return;
        }

        SelectedSessionOption = option;
        Prompt = prompt;
        await Run();
    }

    [RelayCommand]
    private async Task ContinueRun(AgentPanelRunViewModel? run)
    {
        if (run == null || IsRunning || !run.CanContinue)
            return;

        var option = SessionOptions.FirstOrDefault(item =>
            string.Equals(
                item.Content?.ToString(),
                run.SessionId,
                StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            StatusText = DisconnectedText;
            return;
        }

        SelectedSessionOption = option;
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunResumeResult>(
                    AgentRuntimeMethodNames.RunResume,
                    new { runId = run.RunId },
                    requestId: $"agent-panel-resume-{Guid.NewGuid():N}")
                .ConfigureAwait(false);
            if (!result.Resumed)
            {
                StatusText = result.Error ?? ErrorText;
                return;
            }

            _activeRunId = result.RunId;
            _lastRunSequence = 0;
            _isRecoveringRun = false;
            _runRecoveryRequested = false;
            _currentAssistantMessage = null;
            _toolMessages.Clear();
            IsStopping = false;
            IsCanceling = false;
            IsAppending = false;
            IsRunning = true;
            StatusText = RunningText;
            ApplyRunCheckpoint(null);
            StartRunTracking(
                DateTimeOffset.UtcNow,
                modelRequestCount: 1,
                toolCallCount: 0,
                sessionName: SelectedSession?.Name ?? string.Empty);
            StartRunRecovery(result.RunId, 0);
            RefreshRunHistory();
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryRuntime))]
    private void RetryRuntime()
    {
        RefreshSessions();
    }

    public async Task<bool> TryAddAttachmentAsync(string path)
    {
        if (PendingAttachments.Count >= MaximumPendingAttachments)
        {
            StatusText = Text("Agent.AttachmentLimit");
            return false;
        }

        try
        {
            var attachment = await AgentAttachmentViewModel.FromFileAsync(path);
            if (PendingAttachments.Any(item =>
                    string.Equals(item.FileName, attachment.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = Text("Agent.AttachmentAlreadyAdded");
                return false;
            }

            PendingAttachments.Add(attachment);
            OnPropertyChanged(nameof(HasPendingAttachments));
            NotifyRunCommands();
            return true;
        }
        catch (NotSupportedException)
        {
            StatusText = Text("Agent.UnsupportedAttachment");
        }
        catch (InvalidDataException)
        {
            StatusText = Text("Agent.AttachmentTooLarge");
        }
        catch (IOException)
        {
            StatusText = Text("Agent.AttachmentReadFailed");
        }

        return false;
    }

    public async Task<bool> TryAddClipboardImageAsync(byte[] pngBytes)
    {
        if (PendingAttachments.Count >= MaximumPendingAttachments)
        {
            StatusText = Text("Agent.AttachmentLimit");
            return false;
        }

        try
        {
            var attachment = AgentAttachmentViewModel.FromImageBytes(
                "clipboard.png",
                "image/png",
                pngBytes);
            PendingAttachments.Add(attachment);
            OnPropertyChanged(nameof(HasPendingAttachments));
            NotifyRunCommands();
            return true;
        }
        catch (InvalidDataException)
        {
            StatusText = Text("Agent.AttachmentTooLarge");
            return false;
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AgentAttachmentViewModel? attachment)
    {
        if (attachment == null || !PendingAttachments.Remove(attachment))
            return;

        OnPropertyChanged(nameof(HasPendingAttachments));
        NotifyRunCommands();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        var selectedSession = SelectedSession;
        var promptText = Prompt.Trim();
        if (selectedSession == null || !selectedSession.IsConnected ||
            (promptText.Length == 0 && !HasPendingAttachments))
            return;

        if (promptText.Length > MaximumPromptCharacters)
            promptText = promptText[..MaximumPromptCharacters];

        var pendingAttachments = PendingAttachments.ToArray();
        var modelPrompt = promptText.Length == 0
            ? Text("Agent.AttachmentOnlyPrompt")
            : promptText;
        var userMessage = new AgentChatMessage(
            "user",
            modelPrompt,
            ContentParts: pendingAttachments.Select(item => item.ContentPart).ToArray());
        var requestMessages = new List<AgentChatMessage>
        {
            new("system", SystemPrompt)
        };
        requestMessages.AddRange(_conversation);
        requestMessages.Add(userMessage);

        // Provider request timeout controls one model call; the run itself needs
        // a separate budget so long remote commands can finish and be reviewed.
        var timeout = AgentRunCoordinator.DefaultRunTimeout;
        var runId = $"cxshell-ui-{Guid.NewGuid():N}";
        _activeRunId = runId;
        _runPrompts[runId] = modelPrompt;
        _lastRunSequence = 0;
        _isRecoveringRun = false;
        _runRecoveryRequested = false;
        _currentAssistantMessage = null;
        _toolMessages.Clear();
        RestoreRunSteps(null);
        IsStopping = false;
        IsCanceling = false;
        IsAppending = false;
        ApplyRunCheckpoint(null);
        IsRunning = true;
        StatusText = RunningText;
        StartRunTracking(
            DateTimeOffset.UtcNow,
            modelRequestCount: 1,
            toolCallCount: 0,
            sessionName: selectedSession.Name);
        AddMessage(AgentPanelMessageViewModel.User(modelPrompt, pendingAttachments));

        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunResult>(
                    AgentRuntimeMethodNames.Run,
                    new
                    {
                        runId,
                        sessionId = selectedSession.SessionId.ToString("D"),
                        messages = requestMessages,
                        timeoutMs = (int)timeout.TotalMilliseconds
                    },
                    requestId: $"agent-panel-start-{Guid.NewGuid():N}");
            if (!result.Started)
            {
                FailRun(ErrorText);
                return;
            }

            _conversation.Add(userMessage);
            TrimConversation();
            Prompt = string.Empty;
            PendingAttachments.Clear();
            OnPropertyChanged(nameof(HasPendingAttachments));
            RefreshActiveRun();
            return;
        }
        catch (Exception exception)
        {
            FailRun(exception.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAppend))]
    private async Task Append()
    {
        var runId = _activeRunId;
        var promptText = Prompt.Trim();
        if (string.IsNullOrWhiteSpace(runId) ||
            (promptText.Length == 0 && !HasPendingAttachments))
        {
            return;
        }

        if (promptText.Length > MaximumPromptCharacters)
            promptText = promptText[..MaximumPromptCharacters];

        var pendingAttachments = PendingAttachments.ToArray();
        var modelPrompt = promptText.Length == 0
            ? Text("Agent.AttachmentOnlyPrompt")
            : promptText;
        var userMessage = new AgentChatMessage(
            "user",
            modelPrompt,
            ContentParts: pendingAttachments.Select(item => item.ContentPart).ToArray());

        IsAppending = true;
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunAppendResult>(
                    AgentRuntimeMethodNames.RunAppend,
                    new
                    {
                        runId,
                        messages = new[] { userMessage }
                    },
                    requestId: $"agent-panel-append-{Guid.NewGuid():N}");

            if (!string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                return;

            if (!result.Appended)
            {
                StatusText = result.Error ?? ErrorText;
                return;
            }

            _conversation.Add(userMessage);
            TrimConversation();
            AddMessage(AgentPanelMessageViewModel.User(modelPrompt, pendingAttachments));
            Prompt = string.Empty;
            PendingAttachments.Clear();
            OnPropertyChanged(nameof(HasPendingAttachments));
            StatusText = FollowUpQueuedText;
        }
        catch (Exception exception)
        {
            if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                StatusText = exception.Message;
        }
        finally
        {
            if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
            {
                IsAppending = false;
                NotifyRunCommands();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanFocusRun))]
    private async Task FocusRun(AgentPanelRunViewModel? run)
    {
        if (run == null || !CanFocusRun(run))
            return;

        var runId = run.RunId;
        _backgroundRunControls.Add(runId);
        NotifyRunCommands();
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunStatusResult>(
                    AgentRuntimeMethodNames.RunStatus,
                    new { runId },
                    requestId: $"agent-panel-focus-{Guid.NewGuid():N}");

            if (!result.Found || result.Run == null || !IsActiveRun(result.Run))
            {
                StatusText = Text("Agent.HistoryRunUnavailable");
                RefreshRunHistory();
                return;
            }

            var option = SessionOptions.FirstOrDefault(item =>
                string.Equals(
                    item.Content?.ToString(),
                    result.Run.SessionId,
                    StringComparison.OrdinalIgnoreCase));
            if (option == null)
            {
                StatusText = DisconnectedText;
                return;
            }

            if (IsRunning || _activeRunId != null)
                return;

            SelectedSessionOption = option;
            AttachActiveRun(result.Run);
            SelectedRun = run;
            RefreshRunHistory();
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            _backgroundRunControls.Remove(runId);
            NotifyRunCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageBackgroundRun))]
    private async Task StopBackgroundRun(AgentPanelRunViewModel? run)
    {
        if (run == null || !CanManageBackgroundRun(run))
            return;

        var runId = run.RunId;
        _backgroundRunControls.Add(runId);
        StatusText = StoppingText;
        NotifyRunCommands();
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunStopResult>(
                    AgentRuntimeMethodNames.RunStop,
                    new { runId },
                    requestId: $"agent-panel-background-stop-{Guid.NewGuid():N}");
            StatusText = result.Requested ? StoppingText : result.Error ?? ErrorText;
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            _backgroundRunControls.Remove(runId);
            NotifyRunCommands();
            RefreshRunHistory();
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageBackgroundRun))]
    private async Task CancelBackgroundRun(AgentPanelRunViewModel? run)
    {
        if (run == null || !CanManageBackgroundRun(run))
            return;

        var runId = run.RunId;
        _backgroundRunControls.Add(runId);
        StatusText = CancellingText;
        NotifyRunCommands();
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeCancelResult>(
                    AgentRuntimeMethodNames.Cancel,
                    new { runId },
                    requestId: $"agent-panel-background-cancel-{Guid.NewGuid():N}");
            StatusText = result.Cancelled ? CancellingText : result.Error ?? ErrorText;
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            _backgroundRunControls.Remove(runId);
            NotifyRunCommands();
            RefreshRunHistory();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        var runId = _activeRunId;
        if (string.IsNullOrWhiteSpace(runId))
            return;

        IsStopping = true;
        StatusText = StoppingText;
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunStopResult>(
                    AgentRuntimeMethodNames.RunStop,
                    new { runId },
                    requestId: $"agent-panel-stop-{Guid.NewGuid():N}");

            if (!string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                return;

            if (!result.Requested)
            {
                IsStopping = false;
                StatusText = result.Error ?? ErrorText;
            }
            else
            {
                StatusText = StoppingText;
            }
        }
        catch (Exception exception)
        {
            if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
            {
                IsStopping = false;
                StatusText = exception.Message;
            }
        }
        finally
        {
            if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                NotifyRunCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task Cancel()
    {
        var runId = _activeRunId;
        if (string.IsNullOrWhiteSpace(runId))
            return;

        IsCanceling = true;
        StatusText = CancellingText;
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeCancelResult>(
                    AgentRuntimeMethodNames.Cancel,
                    new { runId },
                    requestId: $"agent-panel-cancel-{Guid.NewGuid():N}");

            if (!string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                return;

            if (!result.Cancelled)
            {
                IsCanceling = false;
                StatusText = result.Error ?? ErrorText;
            }
            else
            {
                StatusText = CancellingText;
            }
        }
        catch (Exception exception)
        {
            if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
            {
                IsCanceling = false;
                StatusText = exception.Message;
            }
        }
        finally
        {
            if (string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                NotifyRunCommands();
        }
    }

    private void OnRuntimeEvent(AgentRuntimeEventEnvelope envelope)
    {
        if (!string.Equals(envelope.EventName, "run", StringComparison.Ordinal) ||
            envelope.Payload is not { } payload)
        {
            return;
        }

        AgentRuntimeStreamEnvelope? stream;
        try
        {
            stream = payload.Deserialize<AgentRuntimeStreamEnvelope>(RuntimeJsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (stream != null)
            OnRuntimeEvent(stream);
    }

    private void OnRuntimeEvent(AgentRuntimeStreamEnvelope envelope)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRuntimeEvent(envelope));
            return;
        }

        UpdateRunActivity(envelope);

        if (!string.Equals(_activeRunId, envelope.RunId, StringComparison.Ordinal))
            return;

        if (_isRecoveringRun)
        {
            _runRecoveryRequested = true;
            return;
        }

        if (envelope.Sequence <= _lastRunSequence)
            return;

        if (envelope.Sequence > _lastRunSequence + 1)
        {
            StartRunRecovery(envelope.RunId, _lastRunSequence);
            return;
        }

        ApplyRunEvents(envelope);
        _lastRunSequence = _activeRunId == envelope.RunId ? envelope.Sequence : 0;
    }

    private void ApplyRunEvents(AgentRuntimeStreamEnvelope envelope)
    {
        foreach (var @event in envelope.Events)
        {
            if (@event.Checkpoint != null)
                ApplyRunCheckpoint(@event.Checkpoint);
            if (@event.Step != null)
                UpdateRunStep(@event);

            switch (@event.Type)
            {
                case "run_start":
                    StatusText = IsCanceling
                        ? CancellingText
                        : IsStopping
                            ? StoppingText
                            : RunningText;
                    break;
                case "run_message_appended":
                    StatusText = FollowUpQueuedText;
                    break;
                case "run_step":
                    UpdateRunStep(@event);
                    break;
                case "text_delta":
                    AppendAssistantText(@event.Text);
                    break;
                case "tool_call_update":
                    UpdateToolCall(@event);
                    break;
                case "command_progress":
                    UpdateToolProgress(@event);
                    break;
                case "command_output_delta":
                    UpdateToolOutput(@event);
                    break;
                case "command_output_truncated":
                    UpdateToolOutput(@event with
                    {
                        Text = string.IsNullOrWhiteSpace(@event.Message)
                            ? "[command output truncated]"
                            : $"[{@event.Message}]"
                    });
                    break;
                case "tool_call_approval_required":
                    ApplyRunPhase(@event);
                    UpdateToolCall(@event);
                    StatusText = PendingApprovalText;
                    break;
                case "credential_required":
                    ApplyRunPhase(@event);
                    UpdateCredentialRequest(@event);
                    StatusText = WaitingForInputText;
                    break;
                case "tool_call_result":
                    UpdateToolResult(@event);
                    break;
                case "tool_verification":
                    UpdateToolVerification(@event);
                    break;
                case "run_phase":
                    ApplyRunPhase(@event);
                    break;
                case "error":
                    AddMessage(AgentPanelMessageViewModel.Error(
                        string.IsNullOrWhiteSpace(@event.Message) ? ErrorText : @event.Message));
                    StatusText = @event.Message ?? ErrorText;
                    break;
                case "loop_end":
                    CompleteRun(@event.Reason);
                    break;
            }
        }
    }

    private void StartRunRecovery(string? runId, long afterSequence)
    {
        if (string.IsNullOrWhiteSpace(runId) ||
            !string.Equals(_activeRunId, runId, StringComparison.Ordinal) ||
            _isRecoveringRun)
        {
            return;
        }

        _isRecoveringRun = true;
        _runRecoveryRequested = false;
        _ = RecoverRunEventsAsync(runId, Math.Max(0, afterSequence));
    }

    private async Task RecoverRunEventsAsync(string runId, long afterSequence)
    {
        var recovered = new List<AgentRuntimeStreamEnvelope>();
        var cursor = afterSequence;

        try
        {
            while (true)
            {
                var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunEventsResult>(
                        AgentRuntimeMethodNames.RunEvents,
                        new
                        {
                            runId,
                            afterSequence = cursor,
                            limit = AgentRunCoordinator.MaximumEventReadLimit
                        },
                        requestId: $"agent-panel-events-{Guid.NewGuid():N}")
                    .ConfigureAwait(false);

                foreach (var envelope in result.Events
                             .Where(item => item.Sequence > cursor)
                             .OrderBy(item => item.Sequence))
                {
                    recovered.Add(envelope);
                }

                if (!result.HasMore || result.NextSequence <= cursor)
                    break;

                cursor = result.NextSequence;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                    return;

                foreach (var envelope in recovered)
                {
                    if (envelope.Sequence <= _lastRunSequence)
                        continue;

                    ApplyRunEvents(envelope);
                    _lastRunSequence = _activeRunId == runId ? envelope.Sequence : 0;
                    if (_activeRunId == null)
                        break;
                }
            });
        }
        catch
        {
            // Live events remain the primary path. A temporary replay failure
            // must not make the panel itself fail or interrupt the run.
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(_activeRunId, runId, StringComparison.Ordinal))
                {
                    _isRecoveringRun = false;
                    _runRecoveryRequested = false;
                    return;
                }

                _isRecoveringRun = false;
                if (_runRecoveryRequested)
                {
                    _runRecoveryRequested = false;
                    StartRunRecovery(runId, _lastRunSequence);
                }
            });
        }
    }

    private void AppendAssistantText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _currentAssistantMessage ??= AddMessage(AgentPanelMessageViewModel.Assistant(string.Empty));
        _currentAssistantMessage.AppendText(text, MaximumTranscriptCharacters);
    }

    private void ApplyRunPhase(AgentRuntimeStreamEvent @event)
    {
        if (!string.IsNullOrWhiteSpace(@event.Phase))
            ActiveRunPhaseText = AgentCheckpointDisplay.PhaseText(@event.Phase, @event.Status);

        var detail = @event.RequiresUserAction
            ? @event.PauseReason ?? @event.Message
            : @event.Message;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            ActiveRunCheckpointText = detail;
            HasActiveRunCheckpoint = true;
        }
    }

    private static string BuildRunDetails(AgentRuntimeRunEventsResult result)
    {
        if (result.Events.Count == 0)
            return result.HasGap
                ? Text("Agent.HistoryEventsGap")
                : "No event details were retained for this run.";

        var lines = new List<string>();
        foreach (var envelope in result.Events.OrderBy(item => item.Sequence))
        {
            foreach (var @event in envelope.Events)
            {
                var detail = @event.Type switch
                {
                    "tool_call_update" => $"tool: {@event.ToolName ?? "unknown"} ({@event.Status ?? "running"})",
                    "tool_call_approval_required" => $"approval: {@event.ToolName ?? "unknown"} (pending)",
                    "tool_call_result" => $"tool result: {@event.ToolName ?? "unknown"} ({@event.Status ?? "completed"}, {@event.DurationMs ?? 0} ms)",
                    "tool_verification" => $"verification: {FormatVerificationStatus(@event.Status)} - {@event.Message ?? "unknown"}",
                    "run_phase" => $"phase: {AgentCheckpointDisplay.PhaseText(@event.Phase, @event.Status)} - {@event.Message ?? ""}".TrimEnd(' ', '-'),
                    "credential_required" => $"credential: {@event.CredentialKind ?? "input"} (pending)",
                    "run_checkpoint" => @event.Checkpoint is { } checkpoint
                        ? $"checkpoint: {AgentCheckpointDisplay.PhaseText(checkpoint)} ({AgentCheckpointDisplay.ProgressText(checkpoint)})"
                        : "checkpoint",
                    "request_retry" => $"provider retry: {@event.ErrorType ?? "temporary"} (attempt {@event.Attempt ?? 0}/{@event.MaxAttempts ?? 0})",
                    "error" => $"error: {@event.ErrorType ?? "unknown"} - {@event.Message ?? "unknown error"}",
                    "loop_end" => $"finished: {@event.Reason ?? "completed"}",
                    _ => @event.Type
                };
                lines.Add($"#{envelope.Sequence}  {detail}");
            }
        }

        if (result.HasGap)
            lines.Insert(0, Text("Agent.HistoryEventsGap"));

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatVerificationStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "verified" => Text("Agent.VerificationVerified"),
            "failed" => Text("Agent.VerificationFailed"),
            _ => Text("Agent.VerificationUnknown")
        };

    private void UpdateToolCall(AgentRuntimeStreamEvent @event)
    {
        var toolCallId = @event.ToolCallId ?? Guid.NewGuid().ToString("N");
        if (!_toolMessages.TryGetValue(toolCallId, out var message))
        {
            message = AgentPanelMessageViewModel.Tool(string.Empty);
            message.ToolCallId = toolCallId;
            _toolMessages[toolCallId] = AddMessage(message);
        }

        message.ToolCallId = toolCallId;
        message.ToolName = @event.ToolName ?? string.Empty;
        message.ToolInput = @event.Input ?? string.Empty;
        message.RiskText = @event.Risk ?? string.Empty;
        message.ApprovalSessionText = @event.SessionName ?? string.Empty;
        message.ApprovalTimeoutText = @event.TimeoutMs is { } timeout
            ? $"{timeout} ms"
            : string.Empty;
        message.IsApprovalPending = string.Equals(
            @event.Status,
            "pending_approval",
            StringComparison.OrdinalIgnoreCase);
        message.StatusText = message.IsApprovalPending
            ? ApprovalRequiredText
            : @event.Status ?? RunningText;
        _activeRunToolCallCount = Math.Max(_activeRunToolCallCount, _toolMessages.Count);
    }

    private void UpdateToolResult(AgentRuntimeStreamEvent @event)
    {
        var toolCallId = @event.ToolCallId ?? string.Empty;
        if (!_toolMessages.TryGetValue(toolCallId, out var message))
        {
            message = AddMessage(AgentPanelMessageViewModel.Tool(
                @event.Result ?? string.Empty));
            if (toolCallId.Length > 0)
                _toolMessages[toolCallId] = message;
        }

        message.ToolName = @event.ToolName ?? message.ToolName;
        message.Content = LimitTranscript(FormatToolResult(@event.Result ?? string.Empty));
        message.IsApprovalPending = false;
        message.DurationText = @event.DurationMs is { } duration
            ? $"{duration} ms"
            : string.Empty;
        message.StatusText = @event.Status ?? CompletedText;
        _activeRunToolCallCount = Math.Max(_activeRunToolCallCount, _toolMessages.Count);
    }

    private void UpdateToolVerification(AgentRuntimeStreamEvent @event)
    {
        var toolCallId = @event.ToolCallId ?? string.Empty;
        if (!_toolMessages.TryGetValue(toolCallId, out var message))
        {
            message = AgentPanelMessageViewModel.Tool(string.Empty);
            message.ToolCallId = toolCallId;
            _toolMessages[toolCallId] = AddMessage(message);
        }

        message.VerificationStatus = @event.Status ?? "unknown";
        var statusText = FormatVerificationStatus(@event.Status);
        message.VerificationText = string.IsNullOrWhiteSpace(@event.Message)
            ? statusText
            : $"{statusText}: {@event.Message}";
    }

    private void UpdateToolProgress(AgentRuntimeStreamEvent @event)
    {
        var toolCallId = @event.ToolCallId ?? string.Empty;
        if (!_toolMessages.TryGetValue(toolCallId, out var message))
            return;

        message.StatusText = @event.Status ?? RunningText;
        if (@event.ElapsedMs is { } elapsedMs)
        {
            message.DurationText = AgentPanelViewModel.FormatDuration(
                TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs)));
        }
    }

    private void UpdateToolOutput(AgentRuntimeStreamEvent @event)
    {
        var toolCallId = @event.ToolCallId ?? string.Empty;
        if (!_toolMessages.TryGetValue(toolCallId, out var message))
        {
            message = AgentPanelMessageViewModel.Tool(string.Empty);
            message.ToolCallId = toolCallId;
            message.ToolName = @event.ToolName ?? string.Empty;
            _toolMessages[toolCallId] = AddMessage(message);
        }

        message.AppendToolOutput(@event.Text ?? @event.Message ?? string.Empty, MaximumTranscriptCharacters);
        message.StatusText = @event.Stream == "stderr" ? "stderr" : RunningText;
        if (@event.ElapsedMs is { } elapsedMs)
        {
            message.DurationText = AgentPanelViewModel.FormatDuration(
                TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs)));
        }
    }

    private void UpdateCredentialRequest(AgentRuntimeStreamEvent @event)
    {
        var toolCallId = @event.ToolCallId ?? string.Empty;
        if (!_toolMessages.TryGetValue(toolCallId, out var message))
        {
            message = AgentPanelMessageViewModel.Tool(string.Empty);
            message.ToolCallId = toolCallId;
            _toolMessages[toolCallId] = AddMessage(message);
        }

        message.ToolName = @event.ToolName ?? message.ToolName;
        message.CredentialRequestId = @event.CredentialRequestId ?? string.Empty;
        message.CredentialKind = @event.CredentialKind ?? "password";
        message.CredentialPrompt = string.IsNullOrWhiteSpace(@event.CredentialPrompt)
            ? CredentialRequiredText
            : @event.CredentialPrompt;
        message.CredentialValue = string.Empty;
        message.RememberCredential = true;
        message.IsCredentialPending = message.CredentialRequestId.Length > 0;
        message.StatusText = message.IsCredentialPending
            ? CredentialRequiredText
            : @event.Status ?? RunningText;
    }

    internal static string FormatToolResult(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return value;

            if (root.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                root.TryGetProperty("targetCount", out _))
            {
                return FormatFleetInspection(root);
            }

            if (root.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(output.GetString()))
            {
                return output.GetString()!.TrimEnd('\r', '\n');
            }

            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? value;
            }
        }
        catch (JsonException)
        {
        }

        return value;
    }

    private static string FormatFleetInspection(JsonElement root)
    {
        var targetCount = GetInt32(root, "targetCount");
        var successCount = GetInt32(root, "successCount");
        var failureCount = GetInt32(root, "failureCount");
        var lines = new List<string>
        {
            $"Fleet inspection: {targetCount} target{(targetCount == 1 ? string.Empty : "s")}, " +
            $"{successCount} succeeded, {failureCount} failed.",
            string.Empty,
            "Target | Platform | Status"
        };

        foreach (var item in root.GetProperty("results").EnumerateArray())
        {
            var name = GetSingleLine(item, "name");
            var host = GetSingleLine(item, "host");
            var platform = GetSingleLine(item, "platform");
            var status = GetSingleLine(item, "status");
            var target = string.IsNullOrWhiteSpace(name) ? host : $"{name} ({host})";
            lines.Add($"{target} | {platform} | {status}");
        }

        lines.Add(string.Empty);
        lines.Add("Detailed output is available in each target Terminal.");
        return string.Join(Environment.NewLine, lines);
    }

    private static int GetInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt32(out var value)
            ? value
            : 0;

    private static string GetSingleLine(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return "-";
        }

        var value = property.GetString()?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    [RelayCommand]
    private async Task ApproveTool(string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(_activeRunId) || string.IsNullOrWhiteSpace(toolCallId))
            return;

        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunApprovalResult>(
                    AgentRuntimeMethodNames.RunApprove,
                    new { runId = _activeRunId, toolCallId },
                    requestId: $"agent-panel-approve-{Guid.NewGuid():N}");
            if (_toolMessages.TryGetValue(toolCallId, out var message))
            {
                message.IsApprovalPending = false;
                message.StatusText = result.Approved ? RunningText : result.Error ?? ErrorText;
            }
        }
        catch (Exception exception)
        {
            if (_toolMessages.TryGetValue(toolCallId, out var message))
                message.StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DenyTool(string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(_activeRunId) || string.IsNullOrWhiteSpace(toolCallId))
            return;

        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunApprovalResult>(
                    AgentRuntimeMethodNames.RunDeny,
                    new { runId = _activeRunId, toolCallId },
                    requestId: $"agent-panel-deny-{Guid.NewGuid():N}");
            if (_toolMessages.TryGetValue(toolCallId, out var message))
            {
                message.IsApprovalPending = false;
                message.StatusText = result.Decided ? ApprovalDeniedText : result.Error ?? ErrorText;
            }
        }
        catch (Exception exception)
        {
            if (_toolMessages.TryGetValue(toolCallId, out var message))
                message.StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task SubmitCredential(AgentPanelMessageViewModel? message)
    {
        if (message == null ||
            !message.IsCredentialPending ||
            string.IsNullOrWhiteSpace(_activeRunId) ||
            string.IsNullOrWhiteSpace(message.CredentialRequestId) ||
            string.IsNullOrEmpty(message.CredentialValue))
        {
            return;
        }

        var value = message.CredentialValue;
        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunCredentialResult>(
                    AgentRuntimeMethodNames.RunCredential,
                    new
                    {
                        runId = _activeRunId,
                        credentialRequestId = message.CredentialRequestId,
                        value,
                        rememberForRun = message.RememberCredential
                    },
                    requestId: $"agent-panel-credential-{Guid.NewGuid():N}");
            message.ClearCredentialValue();
            if (result.Provided)
            {
                message.IsCredentialPending = false;
                message.StatusText = RunningText;
            }
            else
            {
                message.StatusText = result.Error ?? ErrorText;
            }
        }
        catch (Exception exception)
        {
            message.StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task DenyCredential(AgentPanelMessageViewModel? message)
    {
        if (message == null ||
            !message.IsCredentialPending ||
            string.IsNullOrWhiteSpace(_activeRunId) ||
            string.IsNullOrWhiteSpace(message.CredentialRequestId))
        {
            return;
        }

        try
        {
            var result = await _runtimeClient.SendResultAsync<AgentRuntimeRunCredentialResult>(
                    AgentRuntimeMethodNames.RunCredentialDeny,
                    new
                    {
                        runId = _activeRunId,
                        credentialRequestId = message.CredentialRequestId
                    },
                    requestId: $"agent-panel-credential-deny-{Guid.NewGuid():N}");
            message.ClearCredentialValue();
            message.IsCredentialPending = false;
            message.StatusText = result.Provided ? CancelledText : result.Error ?? ErrorText;
        }
        catch (Exception exception)
        {
            message.StatusText = exception.Message;
        }
    }

    private void FailRun(string message)
    {
        StopRunTracking();
        _activeRunId = null;
        _lastRunSequence = 0;
        _isRecoveringRun = false;
        _runRecoveryRequested = false;
        ApplyRunCheckpoint(null);
        IsRunning = false;
        StatusText = string.IsNullOrWhiteSpace(message) ? ErrorText : message;
        AddMessage(AgentPanelMessageViewModel.Error(StatusText));
        NotifyRunCommands();
        RefreshRunHistory();
    }

    private void CompleteRun(string? reason)
    {
        var finalResponse = _currentAssistantMessage?.Content;
        if (_currentAssistantMessage is { Content.Length: > 0 } assistant)
        {
            _conversation.Add(new AgentChatMessage("assistant", assistant.Content));
            TrimConversation();
        }

        StatusText = reason switch
        {
            "completed" => CompletedText,
            "stopped" => StoppedText,
            "aborted" => CancelledText,
            "timeout" => Text("Agent.TimedOut"),
            "provider_error" or "session_unavailable" or "error" or "limits" or "max_iterations" => ErrorText,
            _ => reason ?? CompletedText
        };
        var runId = _activeRunId ?? string.Empty;
        var durationText = StopRunTracking();
        if (runId.Length > 0)
        {
            AddMessage(AgentPanelMessageViewModel.Summary(
                runId,
                _activeRunSessionName,
                StatusText,
                durationText,
                _activeRunToolCallCount,
                _activeRunModelRequestCount,
                string.IsNullOrWhiteSpace(finalResponse)
                    ? Text("Agent.SummaryNoResult")
                    : LimitTranscript(finalResponse)));
        }
        IsRunning = false;
        _activeRunId = null;
        _lastRunSequence = 0;
        _isRecoveringRun = false;
        _runRecoveryRequested = false;
        _currentAssistantMessage = null;
        _toolMessages.Clear();
        ApplyRunCheckpoint(null);
        foreach (var message in Messages.Where(message => message.IsCredentialPending))
        {
            message.ClearCredentialValue();
            message.IsCredentialPending = false;
        }
        NotifyRunCommands();
        RefreshRunHistory();
    }

    private AgentPanelMessageViewModel AddMessage(AgentPanelMessageViewModel message)
    {
        Messages.Add(message);
        OnPropertyChanged(nameof(HasMessages));
        return message;
    }

    private void TrimConversation()
    {
        while (_conversation.Count > MaximumConversationMessages)
            _conversation.RemoveAt(0);
    }

    private static string LimitTranscript(string value)
        => value.Length <= MaximumTranscriptCharacters
            ? value
            : value[..MaximumTranscriptCharacters] + "\n[...]";

    private static string BuildSessionHeader(AgentSessionSnapshot session)
    {
        var name = string.IsNullOrWhiteSpace(session.Name) ? session.Host : session.Name;
        var endpoint = string.IsNullOrWhiteSpace(session.Username)
            ? $"{session.Host}:{session.Port}"
            : $"{session.Username}@{session.Host}:{session.Port}";
        return $"{name} · {endpoint}";
    }

    private string GetRunSessionLabel(string sessionId)
        => Guid.TryParse(sessionId, out var id) &&
           _sessionsById.TryGetValue(id, out var session)
            ? BuildSessionHeader(session)
            : sessionId;

    private static string Text(string key) => LocalizationService.Shared.Text(key);

    partial void OnSelectedSessionOptionChanged(ISelectOption? value)
    {
        var nextSessionId = SelectedSessionId;
        if (IsRunning &&
            _conversationSessionId is { } activeSessionId &&
            activeSessionId != nextSessionId)
        {
            SelectedSessionOption = _lastSelectedSessionOption;
            return;
        }

        if (_conversationSessionId != nextSessionId)
        {
            SaveCurrentSessionState();
            RestoreSessionState(nextSessionId);
            _conversationSessionId = nextSessionId;
            StatusText = ReadyText;
        }

        _lastSelectedSessionOption = value;
        OnPropertyChanged(nameof(SelectedSession));
        OnPropertyChanged(nameof(SelectedSessionId));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(IsSelectedSessionConnected));
        OnPropertyChanged(nameof(SelectedSessionStatusText));
        OnPropertyChanged(nameof(IsSessionSelectionEnabled));
        RefreshFilteredRunHistory();
        NotifyRunCommands();
    }

    partial void OnSelectedRunHistoryFilterOptionChanged(ISelectOption? value)
        => RefreshFilteredRunHistory();

    partial void OnRunHistorySearchChanged(string value)
        => RefreshFilteredRunHistory();

    partial void OnProviderTestStatusTextChanged(string value)
        => OnPropertyChanged(nameof(HasProviderTestStatus));

    partial void OnIsTestingProviderChanged(bool value)
    {
        TestProviderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasProviderTestStatus));
    }

    partial void OnActiveRunCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasActiveRuns));
        OnPropertyChanged(nameof(ActivityStatusText));
    }

    partial void OnPromptChanged(string value)
        => NotifyRunCommands();

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSessionSelectionEnabled));
        OnPropertyChanged(nameof(IsRuntimeRetryVisible));
        OnPropertyChanged(nameof(IsRunElapsedVisible));
        if (!value)
        {
            IsStopping = false;
            IsCanceling = false;
            IsAppending = false;
        }

        NotifyRunCommands();
    }

    partial void OnIsStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPromptInputEnabled));
        StopCommand.NotifyCanExecuteChanged();
        AppendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCancelingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPromptInputEnabled));
        CancelCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AppendCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAppendingChanged(bool value)
        => AppendCommand.NotifyCanExecuteChanged();

    partial void OnRuntimeStateChanged(AgentRuntimeSessionState value)
    {
        OnPropertyChanged(nameof(IsRuntimeRetryVisible));
        RetryRuntimeCommand.NotifyCanExecuteChanged();
    }

    private void SaveCurrentSessionState()
    {
        if (_conversationSessionId is not { } sessionId)
            return;

        var state = GetOrCreateSessionState(sessionId);
        state.Conversation.Clear();
        state.Conversation.AddRange(_conversation);
        state.Messages.Clear();
        state.Messages.AddRange(Messages);
    }

    private void RestoreSessionState(Guid? sessionId)
    {
        _conversation.Clear();
        Messages.Clear();
        _toolMessages.Clear();
        _currentAssistantMessage = null;

        if (sessionId is { } id && _sessionStates.TryGetValue(id, out var state))
        {
            _conversation.AddRange(state.Conversation);
            foreach (var message in state.Messages)
            {
                Messages.Add(message);
                if (message.IsTool &&
                    message.ToolCallId is { Length: > 0 } toolCallId &&
                    message.IsApprovalPending)
                {
                    _toolMessages[toolCallId] = message;
                }
            }
        }

        OnPropertyChanged(nameof(HasMessages));
    }

    private void UpdateRunStep(AgentRuntimeStreamEvent @event)
    {
        var step = @event.Step;
        if (step == null || string.IsNullOrWhiteSpace(step.Id))
            return;

        if (!_activeRunStepMap.TryGetValue(step.Id, out var viewModel))
        {
            viewModel = new AgentPanelStepViewModel(step);
            _activeRunStepMap[step.Id] = viewModel;
            ActiveRunSteps.Add(viewModel);
        }
        else
        {
            viewModel.Update(step);
        }

        OnPropertyChanged(nameof(HasActiveRunSteps));
    }

    private void RestoreRunSteps(IReadOnlyList<AgentRunStep>? steps)
    {
        _activeRunStepMap.Clear();
        ActiveRunSteps.Clear();
        foreach (var step in steps ?? [])
        {
            var viewModel = new AgentPanelStepViewModel(step);
            _activeRunStepMap[step.Id] = viewModel;
            ActiveRunSteps.Add(viewModel);
        }

        OnPropertyChanged(nameof(HasActiveRunSteps));
    }

    private SessionAgentState GetOrCreateSessionState(Guid sessionId)
    {
        if (!_sessionStates.TryGetValue(sessionId, out var state))
        {
            state = new SessionAgentState();
            _sessionStates[sessionId] = state;
        }

        return state;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _runtimeSubscription.Dispose();
        if (_runtimeStatusSource != null)
            _runtimeStatusSource.StatusChanged -= OnRuntimeStatusChanged;
        if (!string.IsNullOrWhiteSpace(_activeRunId))
        {
            _ = _runtimeClient.SendAsync(
                AgentRuntimeMethodNames.Cancel,
                new { runId = _activeRunId },
                requestId: $"agent-panel-dispose-{Guid.NewGuid():N}");
        }
        _activeRunId = null;
        StopRunTracking();
        _lastRunSequence = 0;
        _isRecoveringRun = false;
        _runRecoveryRequested = false;
        ApplyRunCheckpoint(null);
        _activeRunIds.Clear();
        _endedRunIds.Clear();
    }

    private sealed class SessionAgentState
    {
        public List<AgentChatMessage> Conversation { get; } = [];
        public List<AgentPanelMessageViewModel> Messages { get; } = [];
    }
}

internal static class AgentCheckpointDisplay
{
    public static string PhaseText(AgentRunCheckpoint checkpoint)
        => $"{Text(GetPhaseKey(checkpoint.Phase))} · {Text(GetStatusKey(checkpoint.Status))}";

    public static string PhaseText(string? phase, string? status)
        => $"{Text(GetPhaseKey(phase))} · {Text(GetStatusKey(status))}";

    public static string ProgressText(AgentRunCheckpoint checkpoint)
    {
        var lastTool = string.IsNullOrWhiteSpace(checkpoint.ToolName)
            ? "-"
            : checkpoint.ToolName;
        return $"{Text("Agent.CheckpointStep")} {checkpoint.Step} · " +
               $"{Text("Agent.CheckpointLastTool")}: {lastTool} · " +
               $"{Text("Agent.CheckpointRequests")}: {checkpoint.ModelRequestCount} · " +
               $"{Text("Agent.CheckpointTools")}: {checkpoint.ToolCallCount}";
    }

    private static string GetPhaseKey(string? phase)
        => phase?.Trim().ToLowerInvariant() switch
        {
            "run" => "Agent.CheckpointPhaseRun",
            "analysis" => "Agent.CheckpointPhaseAnalysis",
            "execution" => "Agent.CheckpointPhaseExecution",
            "model_request" => "Agent.CheckpointPhaseModelRequest",
            "tool_call" => "Agent.CheckpointPhaseToolCall",
            "verification" => "Agent.CheckpointPhaseVerification",
            "summary" => "Agent.CheckpointPhaseSummary",
            "credential" => "Agent.CheckpointPhaseCredential",
            _ => "Agent.CheckpointPhaseUnknown"
        };

    private static string GetStatusKey(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "running" => "Agent.CheckpointStatusRunning",
            "waiting_for_input" or "waiting" or "pending_approval" or "pending_credential" => "Agent.CheckpointStatusWaiting",
            "completed" => "Agent.CheckpointStatusCompleted",
            "failed" => "Agent.CheckpointStatusFailed",
            "interrupted" => "Agent.CheckpointStatusInterrupted",
            "cancelled" => "Agent.CheckpointStatusCancelled",
            "stopped" => "Agent.CheckpointStatusStopped",
            "timed_out" => "Agent.CheckpointStatusTimedOut",
            _ => "Agent.CheckpointStatusUnknown"
        };

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}

public enum AgentPanelMessageKind
{
    User,
    Assistant,
    Tool,
    Error,
    Summary
}

public sealed partial class AgentPanelMessageViewModel : ObservableObject
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string? _toolCallId;
    [ObservableProperty] private string _toolName = string.Empty;
    [ObservableProperty] private string _toolInput = string.Empty;
    [ObservableProperty] private string _durationText = string.Empty;
    [ObservableProperty] private string _riskText = string.Empty;
    [ObservableProperty] private string _approvalSessionText = string.Empty;
    [ObservableProperty] private string _approvalTimeoutText = string.Empty;
    [ObservableProperty] private bool _isApprovalPending;
    [ObservableProperty] private bool _isCredentialPending;
    [ObservableProperty] private string _credentialRequestId = string.Empty;
    [ObservableProperty] private string _credentialKind = string.Empty;
    [ObservableProperty] private string _credentialPrompt = string.Empty;
    [ObservableProperty] private string _credentialValue = string.Empty;
    [ObservableProperty] private bool _rememberCredential;
    [ObservableProperty] private bool _isToolDetailsExpanded;
    [ObservableProperty] private string _runId = string.Empty;
    [ObservableProperty] private string _summarySessionName = string.Empty;
    [ObservableProperty] private string _summaryStatusText = string.Empty;
    [ObservableProperty] private string _summaryDurationText = string.Empty;
    [ObservableProperty] private int _summaryToolCallCount;
    [ObservableProperty] private int _summaryModelRequestCount;
    [ObservableProperty] private string _summaryResultText = string.Empty;
    [ObservableProperty] private string _verificationStatus = string.Empty;
    [ObservableProperty] private string _verificationText = string.Empty;

    private AgentPanelMessageViewModel(
        AgentPanelMessageKind kind,
        string content,
        IReadOnlyList<AgentAttachmentViewModel>? attachments = null)
    {
        Kind = kind;
        _content = content;
        if (attachments is { Count: > 0 })
            Attachments = attachments.ToArray();

        if (kind == AgentPanelMessageKind.Assistant)
            MarkdownBuilder = new ObservableStringBuilder(content);
        else if (kind == AgentPanelMessageKind.Summary)
            SummaryMarkdownBuilder = new ObservableStringBuilder();
    }

    public AgentPanelMessageKind Kind { get; }
    public bool IsUser => Kind == AgentPanelMessageKind.User;
    public bool IsAssistant => Kind == AgentPanelMessageKind.Assistant;
    public bool IsTool => Kind == AgentPanelMessageKind.Tool;
    public bool IsError => Kind == AgentPanelMessageKind.Error;
    public bool IsSummary => Kind == AgentPanelMessageKind.Summary;
    public IReadOnlyList<AgentAttachmentViewModel> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    public ObservableStringBuilder? MarkdownBuilder { get; }
    public ObservableStringBuilder? SummaryMarkdownBuilder { get; }
    public string ApprovalRequiredText => Text("Agent.ApprovalRequired");
    public string CredentialRequiredText => Text("Agent.CredentialRequired");
    public string CredentialPlaceholderText => string.Equals(
        CredentialKind,
        "username",
        StringComparison.OrdinalIgnoreCase)
        ? Text("Agent.CredentialUsernamePlaceholder")
        : string.Equals(CredentialKind, "token", StringComparison.OrdinalIgnoreCase)
            ? Text("Agent.CredentialTokenPlaceholder")
            : Text("Agent.CredentialPlaceholder");
    public bool IsCredentialSecret => !string.Equals(
        CredentialKind,
        "username",
        StringComparison.OrdinalIgnoreCase);
    public bool IsCredentialPlainText => !IsCredentialSecret;
    public string RememberCredentialText => Text("Agent.RememberCredential");
    public string SubmitCredentialText => Text("Agent.SubmitCredential");
    public string ApproveText => Text("Agent.Approve");
    public string DenyText => Text("Agent.Deny");
    public string ToolDetailsButtonText => IsToolDetailsExpanded
        ? Text("Agent.ToolHideDetails")
        : Text("Agent.ToolShowDetails");
    public bool HasVerification => !string.IsNullOrWhiteSpace(VerificationText);
    public bool IsVerificationVerified => string.Equals(
        VerificationStatus,
        "verified",
        StringComparison.OrdinalIgnoreCase);
    public bool IsVerificationFailed => string.Equals(
        VerificationStatus,
        "failed",
        StringComparison.OrdinalIgnoreCase);
    public bool IsVerificationUnknown => !IsVerificationVerified && !IsVerificationFailed;
    public string ToolSummaryText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(ToolName)
                ? Text("Agent.ToolMessage")
                : ToolName;
            return $"{name} · {GetToolStatusText(StatusText)}";
        }
    }
    public string SummaryTitleText => Text("Agent.RunSummary");
    public string SummaryTargetText =>
        $"{Text("Agent.RunTarget")}: {SummarySessionName}";
    public string SummaryMetricsText =>
        $"{Text("Agent.RunTools")}: {SummaryToolCallCount}  " +
        $"{Text("Agent.RunRequests")}: {SummaryModelRequestCount}";
    public string SummaryDurationLabelText => Text("Agent.RunDuration");
    public string SummaryResultLabelText => Text("Agent.RunResult");
    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    public string Label => Kind switch
    {
        AgentPanelMessageKind.User => Text("Agent.UserMessage"),
        AgentPanelMessageKind.Assistant => Text("Agent.AssistantMessage"),
        AgentPanelMessageKind.Tool => Text("Agent.ToolMessage"),
        AgentPanelMessageKind.Summary => Text("Agent.RunSummary"),
        _ => Text("Agent.Error")
    };

    public static AgentPanelMessageViewModel User(
        string content,
        IReadOnlyList<AgentAttachmentViewModel>? attachments = null)
        => new(AgentPanelMessageKind.User, content, attachments);
    public static AgentPanelMessageViewModel Assistant(string content) => new(AgentPanelMessageKind.Assistant, content);
    public static AgentPanelMessageViewModel Tool(string content) => new(AgentPanelMessageKind.Tool, content);
    public static AgentPanelMessageViewModel Error(string content) => new(AgentPanelMessageKind.Error, content);

    public static AgentPanelMessageViewModel Summary(
        string runId,
        string sessionName,
        string statusText,
        string durationText,
        int toolCallCount,
        int modelRequestCount,
        string resultText)
    {
        var message = new AgentPanelMessageViewModel(AgentPanelMessageKind.Summary, string.Empty)
        {
            RunId = runId,
            SummarySessionName = string.IsNullOrWhiteSpace(sessionName)
                ? Text("Agent.NoSession")
                : sessionName,
            SummaryStatusText = statusText,
            SummaryDurationText = string.IsNullOrWhiteSpace(durationText) ? "-" : durationText,
            SummaryToolCallCount = Math.Max(0, toolCallCount),
            SummaryModelRequestCount = Math.Max(0, modelRequestCount),
            SummaryResultText = resultText
        };

        message.SummaryMarkdownBuilder?.Append(resultText);
        return message;
    }

    public void UpdateSummaryMetrics(AgentRuntimeRunSnapshot snapshot)
    {
        if (!IsSummary)
            return;

        SummaryToolCallCount = snapshot.ToolCallCount;
        SummaryModelRequestCount = snapshot.ModelRequestCount;
        if (snapshot.DurationMs is { } durationMs)
            SummaryDurationText = AgentPanelViewModel.FormatDuration(TimeSpan.FromMilliseconds(durationMs));
    }

    public void AppendText(string text, int maximumLength)
    {
        if (string.IsNullOrEmpty(text) || Content.Length >= maximumLength)
            return;

        var remaining = maximumLength - Content.Length;
        var appendedText = text.Length <= remaining ? text : text[..remaining] + "\n[...]";
        Content += appendedText;
        MarkdownBuilder?.Append(appendedText);
    }

    public void AppendToolOutput(string text, int maximumLength)
    {
        if (string.IsNullOrEmpty(text) || Content.Length >= maximumLength)
            return;

        var remaining = maximumLength - Content.Length;
        Content += text.Length <= remaining ? text : text[..remaining] + "\n[...]";
    }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ToolDetailsButtonText));
        OnPropertyChanged(nameof(ToolSummaryText));
        OnPropertyChanged(nameof(ApprovalRequiredText));
        OnPropertyChanged(nameof(CredentialRequiredText));
        OnPropertyChanged(nameof(CredentialPlaceholderText));
        OnPropertyChanged(nameof(IsCredentialSecret));
        OnPropertyChanged(nameof(IsCredentialPlainText));
        OnPropertyChanged(nameof(RememberCredentialText));
        OnPropertyChanged(nameof(SubmitCredentialText));
        OnPropertyChanged(nameof(ApproveText));
        OnPropertyChanged(nameof(DenyText));
        OnPropertyChanged(nameof(HasVerification));
        OnPropertyChanged(nameof(IsVerificationVerified));
        OnPropertyChanged(nameof(IsVerificationFailed));
        OnPropertyChanged(nameof(IsVerificationUnknown));
        OnPropertyChanged(nameof(SummaryTitleText));
        OnPropertyChanged(nameof(SummaryTargetText));
        OnPropertyChanged(nameof(SummaryMetricsText));
        OnPropertyChanged(nameof(SummaryDurationLabelText));
        OnPropertyChanged(nameof(SummaryResultLabelText));
    }

    partial void OnIsToolDetailsExpandedChanged(bool value)
        => OnPropertyChanged(nameof(ToolDetailsButtonText));

    partial void OnToolNameChanged(string value)
        => OnPropertyChanged(nameof(ToolSummaryText));

    partial void OnStatusTextChanged(string value)
        => OnPropertyChanged(nameof(ToolSummaryText));

    partial void OnVerificationStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsVerificationVerified));
        OnPropertyChanged(nameof(IsVerificationFailed));
        OnPropertyChanged(nameof(IsVerificationUnknown));
    }

    partial void OnVerificationTextChanged(string value)
        => OnPropertyChanged(nameof(HasVerification));

    partial void OnCredentialKindChanged(string value)
    {
        OnPropertyChanged(nameof(CredentialPlaceholderText));
        OnPropertyChanged(nameof(IsCredentialSecret));
        OnPropertyChanged(nameof(IsCredentialPlainText));
    }

    public void ClearCredentialValue()
    {
        CredentialValue = string.Empty;
        RememberCredential = false;
    }

    partial void OnSummarySessionNameChanged(string value)
        => OnPropertyChanged(nameof(SummaryTargetText));

    partial void OnSummaryToolCallCountChanged(int value)
        => OnPropertyChanged(nameof(SummaryMetricsText));

    partial void OnSummaryModelRequestCountChanged(int value)
        => OnPropertyChanged(nameof(SummaryMetricsText));

    private string GetToolStatusText(string value)
        => value switch
        {
            "running" => Text("Agent.ToolStatusRunning"),
            "completed" => Text("Agent.ToolStatusCompleted"),
            "failed" => Text("Agent.ToolStatusFailed"),
            _ when string.Equals(value, ApprovalRequiredText, StringComparison.Ordinal) => value,
            _ => string.IsNullOrWhiteSpace(value) ? Text("Agent.ToolStatusRunning") : value
        };

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}

public sealed partial class AgentPanelRunViewModel : ObservableObject
{
    private readonly AgentRuntimeRunSnapshot _snapshot;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private bool _hasLoadedDetails;
    [ObservableProperty] private string _detailsText = string.Empty;

    public AgentPanelRunViewModel(
        AgentRuntimeRunSnapshot snapshot,
        bool canRetry,
        bool canContinue = false,
        string? sessionLabel = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _sessionLabel = string.IsNullOrWhiteSpace(sessionLabel)
            ? snapshot.SessionId
            : sessionLabel;
        CanRetry = canRetry && AgentRunStates.IsFailure(snapshot.Status);
        CanContinue = canContinue &&
                      snapshot.CanResume &&
                      string.Equals(snapshot.Status, "interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private readonly string _sessionLabel;

    public string RunId => _snapshot.RunId;
    public string SessionId => _snapshot.SessionId;
    public string Provider => _snapshot.Provider ?? "-";
    public string Model => _snapshot.Model ?? "-";
    public string PromptPreview => string.IsNullOrWhiteSpace(_snapshot.PromptPreview)
        ? Text("Agent.HistoryNoPrompt")
        : _snapshot.PromptPreview!;
    public string TargetText => $"{Text("Agent.HistoryTarget")}: {_sessionLabel} · {Provider}/{Model}";
    public string Status => _snapshot.Status;
    public bool IsActive => AgentRunStates.IsActive(_snapshot.Status);
    public bool IsWaiting => AgentRunStates.IsWaiting(_snapshot.Status) ||
                             (_snapshot.RequiresUserAction && IsActive);
    public bool IsFailure => AgentRunStates.IsFailure(_snapshot.Status);
    public bool IsFinished => !IsActive;
    public string StatusDisplay => _snapshot.Status switch
    {
        "starting" => Text("Agent.Starting"),
        "waiting_for_input" => Text("Agent.WaitingForInput"),
        "pending_approval" => Text("Agent.PendingApproval"),
        "stopping" => Text("Agent.StoppingShort"),
        "completed" => Text("Agent.Completed"),
        "cancelled" => Text("Agent.Cancelled"),
        "stopped" => Text("Agent.Stopped"),
        "timed_out" => Text("Agent.TimedOut"),
        "interrupted" => Text("Agent.Interrupted"),
        "failed" => Text("Agent.Error"),
        "running" => Text("Agent.Running"),
        _ => _snapshot.Status
    };
    public string TimeText => _snapshot.StartedAtUtc.ToLocalTime().ToString("g");
    public string DurationText => _snapshot.DurationMs is { } duration
        ? $"{duration} ms"
        : "-";
    public bool HasCheckpoint => _snapshot.Checkpoint != null;
    public string CheckpointText => _snapshot.Checkpoint is { } checkpoint
        ? AgentCheckpointDisplay.PhaseText(checkpoint) + " · " + AgentCheckpointDisplay.ProgressText(checkpoint)
        : string.Empty;
    public bool HasPhase => !string.IsNullOrWhiteSpace(_snapshot.Phase);
    public string PhaseText => AgentCheckpointDisplay.PhaseText(_snapshot.Phase, _snapshot.Status);
    public bool HasPauseReason => _snapshot.RequiresUserAction &&
                                  !string.IsNullOrWhiteSpace(_snapshot.PauseReason);
    public string PauseReasonText => _snapshot.PauseReason ?? string.Empty;
    public bool MatchesSearch(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return PromptPreview.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               _sessionLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Provider.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Model.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Status.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               StatusDisplay.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               PhaseText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               PauseReasonText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               ErrorText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
    public string MetricsText
        => $"{Text("Agent.HistoryTools")}: {_snapshot.ToolCallCount}  " +
           $"{Text("Agent.HistoryRequests")}: {_snapshot.ModelRequestCount}  " +
           $"{Text("Agent.HistoryDuration")}: {DurationText}";
    public string ErrorText => string.IsNullOrWhiteSpace(_snapshot.Error)
        ? string.Empty
        : $"{_snapshot.ErrorType ?? Text("Agent.Error")}: {_snapshot.Error}";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool CanRetry { get; }
    public bool CanContinue { get; }
    public string RetryText => Text("Agent.HistoryRetry");
    public string ContinueText => Text("Agent.HistoryContinue");
    public string DetailsButtonText => IsExpanded
        ? Text("Agent.HistoryHideDetails")
        : Text("Agent.HistoryDetails");

    partial void OnIsExpandedChanged(bool value)
        => OnPropertyChanged(nameof(DetailsButtonText));

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(PromptPreview));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(MetricsText));
        OnPropertyChanged(nameof(HasCheckpoint));
        OnPropertyChanged(nameof(CheckpointText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(ContinueText));
        OnPropertyChanged(nameof(DetailsButtonText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsFailure));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(HasPhase));
        OnPropertyChanged(nameof(HasPauseReason));
        OnPropertyChanged(nameof(PauseReasonText));
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}

public sealed partial class AgentPanelStepViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _phase;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string _detail;
    [ObservableProperty] private string _durationText;

    public AgentPanelStepViewModel(AgentRunStep step)
    {
        _title = step.Title;
        _phase = step.Phase;
        _status = step.Status;
        _detail = step.Detail ?? string.Empty;
        _durationText = FormatDuration(step.DurationMs);
    }

    public string StatusDisplay => Status switch
    {
        AgentRunStepStatuses.Running => Text("Agent.StepRunning"),
        AgentRunStepStatuses.Completed => Text("Agent.StepCompleted"),
        AgentRunStepStatuses.Failed => Text("Agent.StepFailed"),
        AgentRunStepStatuses.Waiting => Text("Agent.StepWaiting"),
        AgentRunStepStatuses.Cancelled => Text("Agent.StepCancelled"),
        _ => Text("Agent.StepPending")
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public void Update(AgentRunStep step)
    {
        Title = step.Title;
        Phase = step.Phase;
        Status = step.Status;
        Detail = step.Detail ?? string.Empty;
        DurationText = FormatDuration(step.DurationMs);
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(HasDetail));
    }

    private static string FormatDuration(long? durationMs)
        => durationMs is not { } value
            ? string.Empty
            : value < 1000
                ? $"{Math.Max(0, value)} ms"
                : TimeSpan.FromMilliseconds(Math.Max(0, value)).ToString("mm\\:ss");

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
