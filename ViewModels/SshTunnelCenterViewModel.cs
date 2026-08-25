using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.ViewModels;

public enum SshTunnelAction
{
    Start,
    Stop,
    Restart
}

public sealed partial class SshTunnelItemViewModel : ObservableObject
{
    private readonly SshTunnelCenterViewModel _owner;
    private SshTunnelRuntimeSnapshot _snapshot;

    [ObservableProperty] private bool _isBusy;

    public Guid Id => _snapshot.Id;
    public bool IsRunning => _snapshot.Status == SshTunnelRuntimeStatus.Running;
    public bool IsStopped => _snapshot.Status == SshTunnelRuntimeStatus.Stopped;
    public bool IsError => _snapshot.Status == SshTunnelRuntimeStatus.Error;
    public bool ShowStartAction => IsStopped;
    public bool ShowRestartAction => IsRunning || IsError;
    public bool ShowEditAction => !IsRunning;
    public bool HasError => !string.IsNullOrWhiteSpace(_snapshot.LastError);
    public string ErrorText => _snapshot.LastError ?? string.Empty;
    public string StartText => _owner.StartText;
    public string StopText => _owner.StopText;
    public string RestartText => _owner.RestartText;
    public string EditText => _owner.EditText;
    public string DeleteText => _owner.DeleteText;
    public string DescriptionText => string.IsNullOrWhiteSpace(_snapshot.Description)
        ? $"{TypeText} {_snapshot.ListenPort}"
        : _snapshot.Description;
    public string TypeText => _snapshot.Type switch
    {
        SshTunnelRuleType.Remote => Text("TunnelCenter.TypeRemote"),
        SshTunnelRuleType.Dynamic => Text("TunnelCenter.TypeDynamic"),
        _ => Text("TunnelCenter.TypeLocal")
    };
    public string ListenText => $"{_snapshot.ListenHost}:{_snapshot.ListenPort}";
    public string TargetText => _snapshot.Type == SshTunnelRuleType.Dynamic
        ? Text("TunnelCenter.DynamicTarget")
        : $"{_snapshot.DestinationHost}:{_snapshot.DestinationPort}";
    public string ActivityText => _snapshot.Activity.ConnectionCount == 0
        ? Text("TunnelCenter.NoActivity")
        : string.Format(
            Text("TunnelCenter.ActivitySummary"),
            _snapshot.Activity.ConnectionCount,
            _snapshot.Activity.LastActivityAt?.ToLocalTime().ToString("HH:mm:ss") ?? "-");
    public string ActivityToolTip
    {
        get
        {
            if (_snapshot.Activity.ConnectionCount == 0)
                return Text("TunnelCenter.NoActivityDetail");

            return string.Format(
                Text("TunnelCenter.ActivityDetail"),
                _snapshot.Activity.ConnectionCount,
                _snapshot.Activity.LastActivityAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                _snapshot.Activity.LastOriginator ?? "-");
        }
    }
    public string StatusText => _snapshot.Status switch
    {
        SshTunnelRuntimeStatus.Running when HasError => Text("TunnelCenter.RunningWarning"),
        SshTunnelRuntimeStatus.Running => Text("TunnelCenter.Running"),
        SshTunnelRuntimeStatus.Error => Text("TunnelCenter.Error"),
        _ => Text("TunnelCenter.Stopped")
    };
    public string RuntimeText
    {
        get
        {
            if (!IsRunning || _snapshot.StartedAt == null)
                return "-";

            var elapsed = DateTimeOffset.UtcNow - _snapshot.StartedAt.Value;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;
            return elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed:hh\\:mm\\:ss}"
                : elapsed.ToString("hh\\:mm\\:ss");
        }
    }

    public SshTunnelItemViewModel(
        SshTunnelCenterViewModel owner,
        SshTunnelRuntimeSnapshot snapshot)
    {
        _owner = owner;
        _snapshot = snapshot;
    }

    public void Apply(SshTunnelRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        NotifySnapshotChanged();
    }

    public void UpdateRuntime()
    {
        if (IsRunning)
            OnPropertyChanged(nameof(RuntimeText));
    }

    public void RefreshLocalization()
    {
        NotifySnapshotChanged();
    }

    public void NotifyAvailabilityChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => _owner.ExecuteAsync(this, SshTunnelAction.Start);

    private bool CanStart() => _owner.IsConnected && !IsBusy && IsStopped;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _owner.ExecuteAsync(this, SshTunnelAction.Stop);

    private bool CanStop() => _owner.IsConnected && !IsBusy && IsRunning;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => _owner.ExecuteAsync(this, SshTunnelAction.Restart);

    private bool CanRestart() => _owner.IsConnected && !IsBusy && (IsRunning || IsError);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync() => _owner.EditTunnelAsync(this);

    private bool CanEdit() => !IsBusy && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => _owner.DeleteTunnelAsync(this);

    private bool CanDelete() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        NotifyAvailabilityChanged();
    }

    private void NotifySnapshotChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(ShowStartAction));
        OnPropertyChanged(nameof(ShowRestartAction));
        OnPropertyChanged(nameof(ShowEditAction));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(RestartText));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(ListenText));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(ActivityToolTip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RuntimeText));
        NotifyAvailabilityChanged();
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}

public sealed partial class SshTunnelCenterViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _mainWindow;
    private readonly DispatcherTimer _runtimeTimer;
    private TerminalTabViewModel? _tab;
    private TerminalViewModel? _terminal;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hasSshSession;
    [ObservableProperty] private string _sessionText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<SshTunnelItemViewModel> Tunnels { get; } = new();

    public bool HasTunnels => Tunnels.Count > 0;
    public bool ShowNoSshSession => !HasSshSession;
    public bool ShowDisconnected => HasSshSession && !IsConnected;
    public bool ShowNoRules => HasSshSession && !HasTunnels;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string TitleText => Text("TunnelCenter.Title");
    public string DescriptionText => Text("TunnelCenter.Description");
    public string SessionLabelText => Text("TunnelCenter.Session");
    public string ConnectedText => IsConnected
        ? Text("TunnelCenter.ConnectionActive")
        : Text("TunnelCenter.ConnectionInactive");
    public string NoSshSessionText => Text("TunnelCenter.NoSshSession");
    public string DisconnectedText => Text("TunnelCenter.NotConnected");
    public string NoRulesText => Text("TunnelCenter.NoRules");
    public string EditHintText => Text("TunnelCenter.EditHint");
    public string NameHeaderText => Text("TunnelCenter.Name");
    public string TypeHeaderText => Text("TunnelCenter.Type");
    public string ListenHeaderText => Text("TunnelCenter.Listen");
    public string TargetHeaderText => Text("TunnelCenter.Target");
    public string StatusHeaderText => Text("TunnelCenter.Status");
    public string RuntimeHeaderText => Text("TunnelCenter.Runtime");
    public string ActivityHeaderText => Text("TunnelCenter.Activity");
    public string ActionsHeaderText => Text("TunnelCenter.Actions");
    public string RefreshText => Text("TunnelCenter.Refresh");
    public string CloseText => Text("TunnelCenter.Close");
    public string StartText => Text("TunnelCenter.Start");
    public string StopText => Text("TunnelCenter.Stop");
    public string RestartText => Text("TunnelCenter.Restart");
    public string AddText => Text("TunnelCenter.Add");
    public string EditText => Text("TunnelCenter.Edit");
    public string DeleteText => Text("TunnelCenter.Delete");
    public string DeleteConfirmTitleText => Text("TunnelCenter.DeleteConfirmTitle");
    public string DeleteConfirmMessageText => Text("TunnelCenter.DeleteConfirmMessage");
    public string PortCheckFailedText => Text("TunnelCenter.PortCheckFailed");

    internal Func<SshTunnelRule?, Task<SshTunnelRule?>>? ShowRuleDialogAsync { get; set; }
    internal Func<string, string, Task<bool>>? ConfirmDialogAsync { get; set; }

    public SshTunnelCenterViewModel(MainWindowViewModel mainWindow)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _mainWindow.PropertyChanged += OnMainWindowPropertyChanged;
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;

        _runtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runtimeTimer.Tick += OnRuntimeTimerTick;
        _runtimeTimer.Start();
        BindSelectedTab();
    }

    [RelayCommand]
    private void Refresh()
    {
        StatusMessage = string.Empty;
        RefreshSnapshot();
    }

    [RelayCommand(CanExecute = nameof(CanEditRules))]
    private async Task AddTunnelAsync()
    {
        if (_tab == null || ShowRuleDialogAsync == null)
            return;

        var rule = await ShowRuleDialogAsync(null);
        if (rule == null)
            return;

        var validation = SshTunnelPortChecker.Check(rule, _tab.Session.SshTunnelRules);
        if (validation != null)
        {
            StatusMessage = string.Format(PortCheckFailedText, validation);
            return;
        }

        var rules = _tab.Session.SshTunnelRules
            .Select(SessionEditViewModel.CloneTunnelRule)
            .ToList();
        rules.Add(rule);
        PersistRules(rules);
        StatusMessage = Text("TunnelCenter.RuleAdded");
        RefreshSnapshot();
    }

    [RelayCommand]
    internal async Task EditTunnelAsync(SshTunnelItemViewModel? item)
    {
        if (item == null || item.IsRunning || _tab == null || ShowRuleDialogAsync == null)
            return;

        var source = _tab.Session.SshTunnelRules.FirstOrDefault(rule => rule.Id == item.Id);
        if (source == null)
            return;

        if (item.IsError && _terminal != null && IsConnected)
            await _terminal.StopSshTunnelAsync(item.Id);

        var rule = await ShowRuleDialogAsync(source);
        if (rule == null)
            return;

        var validation = SshTunnelPortChecker.Check(rule, _tab.Session.SshTunnelRules);
        if (validation != null)
        {
            StatusMessage = string.Format(PortCheckFailedText, validation);
            return;
        }

        var rules = _tab.Session.SshTunnelRules
            .Select(SessionEditViewModel.CloneTunnelRule)
            .ToList();
        var index = rules.FindIndex(existing => existing.Id == rule.Id);
        if (index < 0)
            return;

        rules[index] = rule;
        PersistRules(rules);
        StatusMessage = Text("TunnelCenter.RuleUpdated");
        RefreshSnapshot();
    }

    [RelayCommand]
    internal async Task DeleteTunnelAsync(SshTunnelItemViewModel? item)
    {
        if (item == null || _tab == null)
            return;

        if (ConfirmDialogAsync != null &&
            !await ConfirmDialogAsync(DeleteConfirmTitleText, DeleteConfirmMessageText))
        {
            return;
        }

        if (item.IsRunning && _terminal != null && IsConnected)
            await _terminal.StopSshTunnelAsync(item.Id);

        var rules = _tab.Session.SshTunnelRules
            .Where(rule => rule.Id != item.Id)
            .Select(SessionEditViewModel.CloneTunnelRule)
            .ToList();
        PersistRules(rules);
        StatusMessage = Text("TunnelCenter.RuleDeleted");
        RefreshSnapshot();
    }

    private bool CanEditRules()
        => HasSshSession && _tab != null;

    private void PersistRules(IReadOnlyList<SshTunnelRule> rules)
    {
        if (_tab == null)
            return;

        _tab.Session.SshTunnelRules = rules.ToList();
        _mainWindow.SessionTree.UpdateSession(_tab.Session);
    }

    internal async Task ExecuteAsync(SshTunnelItemViewModel item, SshTunnelAction action)
    {
        if (_terminal == null || !IsConnected)
            return;

        var terminal = _terminal;
        item.IsBusy = true;
        SshTunnelOperationResult result;
        try
        {
            result = action switch
            {
                SshTunnelAction.Stop => await terminal.StopSshTunnelAsync(item.Id),
                SshTunnelAction.Restart => await terminal.RestartSshTunnelAsync(item.Id),
                _ => await terminal.StartSshTunnelAsync(item.Id)
            };
        }
        catch (Exception ex)
        {
            result = SshTunnelOperationResult.Failed(ex.Message);
        }
        finally
        {
            item.IsBusy = false;
        }

        if (!ReferenceEquals(_terminal, terminal))
            return;

        StatusMessage = result.Success
            ? action switch
            {
                SshTunnelAction.Stop => Text("TunnelCenter.OperationStopped"),
                SshTunnelAction.Restart => Text("TunnelCenter.OperationRestarted"),
                _ => Text("TunnelCenter.OperationStarted")
            }
            : string.Format(Text("TunnelCenter.OperationFailed"), result.ErrorMessage);
        RefreshSnapshot();
    }

    public void Dispose()
    {
        _runtimeTimer.Stop();
        _runtimeTimer.Tick -= OnRuntimeTimerTick;
        _mainWindow.PropertyChanged -= OnMainWindowPropertyChanged;
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        DetachTerminal();
    }

    private void OnMainWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            BindSelectedTab();
    }

    private void BindSelectedTab()
    {
        DetachTerminal();
        _tab = _mainWindow.SelectedTab;
        HasSshSession = _tab is { IsTerminalSession: true, Session.Protocol: SessionProtocol.SSH };
        _terminal = HasSshSession ? _tab!.Terminal : null;
        if (_terminal != null)
        {
            _terminal.PropertyChanged += OnTerminalPropertyChanged;
            _terminal.SshTunnelRuntimeChanged += OnTunnelRuntimeChanged;
        }

        SessionText = HasSshSession
            ? $"{_tab!.Session.Name}  {_tab.Session.Username}@{_tab.Session.Host}:{_tab.Session.Port}"
            : Text("TunnelCenter.NoSessionValue");
        StatusMessage = string.Empty;
        RefreshSnapshot();
    }

    private void DetachTerminal()
    {
        if (_terminal == null)
            return;

        _terminal.PropertyChanged -= OnTerminalPropertyChanged;
        _terminal.SshTunnelRuntimeChanged -= OnTunnelRuntimeChanged;
        _terminal = null;
    }

    private void OnTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.IsConnected))
            RefreshSnapshot();
    }

    private void OnTunnelRuntimeChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshSnapshot();
        else
            Dispatcher.UIThread.Post(RefreshSnapshot);
    }

    private void RefreshSnapshot()
    {
        IsConnected = _terminal?.IsConnected == true;
        var snapshots = GetSnapshots();
        var remaining = Tunnels.ToDictionary(item => item.Id);

        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (remaining.Remove(snapshot.Id, out var existing))
            {
                existing.Apply(snapshot);
                var currentIndex = Tunnels.IndexOf(existing);
                if (currentIndex != index)
                    Tunnels.Move(currentIndex, index);
            }
            else
            {
                Tunnels.Insert(index, new SshTunnelItemViewModel(this, snapshot));
            }
        }

        foreach (var removed in remaining.Values)
            Tunnels.Remove(removed);

        NotifyStateChanged();
    }

    private IReadOnlyList<SshTunnelRuntimeSnapshot> GetSnapshots()
    {
        if (!HasSshSession || _tab == null)
            return [];

        var runtime = _terminal?.GetSshTunnelRuntimeSnapshot() ?? [];
        if (runtime.Count > 0 || _tab.Session.SshTunnelRules.Count == 0)
            return runtime;

        return _tab.Session.SshTunnelRules.Select(rule => new SshTunnelRuntimeSnapshot(
            rule.Id,
            rule.Type,
            rule.Description,
            ResolveListenHost(rule),
            rule.ListenPort,
            rule.Type == SshTunnelRuleType.Dynamic ? string.Empty : ResolveHost(rule.DestinationHost),
            rule.Type == SshTunnelRuleType.Dynamic ? 0 : rule.DestinationPort,
            SshTunnelRuntimeStatus.Stopped,
            null,
            null)).ToArray();
    }

    private void OnRuntimeTimerTick(object? sender, EventArgs e)
    {
        foreach (var tunnel in Tunnels)
            tunnel.UpdateRuntime();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(SessionLabelText));
        OnPropertyChanged(nameof(ConnectedText));
        OnPropertyChanged(nameof(NoSshSessionText));
        OnPropertyChanged(nameof(DisconnectedText));
        OnPropertyChanged(nameof(NoRulesText));
        OnPropertyChanged(nameof(EditHintText));
        OnPropertyChanged(nameof(NameHeaderText));
        OnPropertyChanged(nameof(TypeHeaderText));
        OnPropertyChanged(nameof(ListenHeaderText));
        OnPropertyChanged(nameof(TargetHeaderText));
        OnPropertyChanged(nameof(StatusHeaderText));
        OnPropertyChanged(nameof(RuntimeHeaderText));
        OnPropertyChanged(nameof(ActivityHeaderText));
        OnPropertyChanged(nameof(ActionsHeaderText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(RestartText));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(DeleteConfirmTitleText));
        OnPropertyChanged(nameof(DeleteConfirmMessageText));
        OnPropertyChanged(nameof(PortCheckFailedText));
        if (!HasSshSession)
            SessionText = Text("TunnelCenter.NoSessionValue");
        foreach (var tunnel in Tunnels)
            tunnel.RefreshLocalization();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDisconnected));
        OnPropertyChanged(nameof(ConnectedText));
        foreach (var tunnel in Tunnels)
            tunnel.NotifyAvailabilityChanged();
    }

    partial void OnHasSshSessionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoSshSession));
        OnPropertyChanged(nameof(ShowDisconnected));
        OnPropertyChanged(nameof(ShowNoRules));
        AddTunnelCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasTunnels));
        OnPropertyChanged(nameof(ShowNoRules));
        OnPropertyChanged(nameof(ConnectedText));
        foreach (var tunnel in Tunnels)
            tunnel.NotifyAvailabilityChanged();
    }

    private static string ResolveListenHost(SshTunnelRule rule)
    {
        if (rule.Type != SshTunnelRuleType.Remote && rule.AcceptLocalConnectionsOnly)
            return "127.0.0.1";
        return string.IsNullOrWhiteSpace(rule.SourceHost) ? "0.0.0.0" : rule.SourceHost.Trim();
    }

    private static string ResolveHost(string? host)
        => string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
