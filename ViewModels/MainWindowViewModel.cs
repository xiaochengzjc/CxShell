using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AtomUI;
using AtomUI.Controls;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CxShell.Services.Agent;
using CxShell.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public enum TabArrangementMode
{
    Single,
    Vertical,
    Horizontal,
    Tile
}

public enum KeyboardBroadcastTarget
{
    CurrentSession,
    AllSessions,
    ConnectedSessions,
    CurrentTabGroup
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const double DefaultSftpPanelWidth = 318;
    private const double MinimumSftpPanelWidth = 120;
    private const double DefaultAgentPanelWidth = 360;
    private const double MinimumAgentPanelWidth = 280;
    private const double MaximumAgentPanelWidth = 600;

    private readonly SessionTreeViewModel _sessionTreeVm;
    private readonly LocalizationService _localization = LocalizationService.Shared;
    private readonly SftpViewModel _emptySftp = new();
    private readonly ServerMonitorViewModel _emptyMonitor = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly ConnectionAuditService _connectionAuditService = new();
    private readonly AgentPermissionPolicy _agentPermissionPolicy;
    private UpdateProgressWindow? _updateProgressWindow;
    private UpdateProgressViewModel? _updateProgressViewModel;
    private SettingsCenterWindow? _settingsCenterWindow;
    private RecentConnectionsWindow? _recentConnectionsWindow;
    private SshTunnelCenterWindow? _sshTunnelCenterWindow;
    private int _disposeState;

    [ObservableProperty] private SessionTreeViewModel _sessionTree;
    [ObservableProperty] private SftpViewModel _sftp = null!;
    [ObservableProperty] private bool _isMonitorVisible = false;
    [ObservableProperty] private bool _isSftpVisible = false;
    [ObservableProperty] private GridLength _sftpPanelWidth = new(0);
    [ObservableProperty] private string _connectionStatusText = "Disconnected";
    [ObservableProperty] private IBrush _connectionStatusColor = Brushes.Gray;
    [ObservableProperty] private string _connectedHostInfo = string.Empty;
    [ObservableProperty] private string _terminalSizeText = "80x24";
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _isTerminalFullScreen;
    [ObservableProperty] private bool _isFullScreenHintVisible;
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private string? _updateProgressText;
    [ObservableProperty] private TabArrangementMode _tabArrangementMode = TabArrangementMode.Single;
    [ObservableProperty] private KeyboardBroadcastTarget _keyboardBroadcastTarget = KeyboardBroadcastTarget.CurrentSession;
    [ObservableProperty] private bool _isTabBarVisible;
    [ObservableProperty] private bool _isAgentPanelVisible;
    [ObservableProperty] private GridLength _agentPanelWidth = new(DefaultAgentPanelWidth);
    private bool _isApplicationSuspended;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();
    public ObservableCollection<TerminalTabGroupViewModel> TabGroups { get; } = new();
    public ObservableCollection<TileTabGroupRowViewModel> TileRows { get; } = new();
    public ObservableCollection<RecentSessionItemViewModel> RecentSessions { get; } = new();
    public CommandPaletteViewModel CommandPalette { get; }
    public IAgentSessionGateway AgentSessionGateway { get; }
    public IAgentRunCoordinator AgentRunCoordinator { get; }
    public IAgentRuntimeSessionAdapter AgentRuntimeSessionAdapter { get; }
    public IAgentRuntimeHost AgentRuntimeHost { get; }
    public AgentRuntimeJsonEndpoint AgentRuntimeJsonEndpoint { get; }
    public IAgentRuntimeTransport AgentRuntimeTransport { get; }
    public IAgentRuntimeClient AgentRuntimeClient { get; }
    public AgentRuntimeSession AgentRuntimeSession { get; }
    public IAgentRuntimeFrameEndpoint AgentRuntimeFrameEndpoint { get; }
    public IAgentRuntimeStreamSession AgentRuntimeStreamSession { get; }
    public AgentPanelViewModel AgentPanel { get; }

    [ObservableProperty] private TerminalTabViewModel? _selectedTab;
    [ObservableProperty] private TerminalTabGroupViewModel? _selectedTabGroup;
    [ObservableProperty] private RecentSessionItemViewModel? _selectedRecentSession;

    public bool HasTabs => Tabs.Count > 0;
    public bool HasRecentSessions => RecentSessions.Count > 0;
    public bool IsMainChromeVisible => !IsTerminalFullScreen;
    public bool IsSftpPanelVisible => IsSftpVisible && !IsTerminalFullScreen;
    public double SftpPanelPixelWidth => SftpPanelWidth.Value;
    public GridLength SftpSplitterWidth => IsSftpPanelVisible ? new GridLength(8) : new GridLength(0);
    public bool IsMonitorPanelVisible => IsMonitorVisible && !IsTerminalFullScreen;
    public bool IsAgentPanelHostVisible => IsAgentPanelVisible && !IsTerminalFullScreen;
    public GridLength AgentSplitterWidth => IsAgentPanelHostVisible ? new GridLength(1) : new GridLength(0);
    public GridLength AgentPanelColumnWidth => IsAgentPanelHostVisible
        ? AgentPanelWidth
        : new GridLength(0, GridUnitType.Pixel);
    public bool IsTabHeaderVisible => !IsTerminalFullScreen;
    public bool IsMainTabHeaderVisible => IsTabHeaderVisible && !IsTabArrangementEnabled;
    public bool IsQuickSessionBarVisible => IsMainChromeVisible && IsTabBarVisible;
    public bool IsSingleTabContentVisible => HasTabs && (!IsTabArrangementEnabled || IsTerminalFullScreen);
    public bool IsArrangedTabsVisible => HasTabs && IsTabArrangementEnabled && !IsTerminalFullScreen;
    public bool IsTabArrangementEnabled => TabArrangementMode != TabArrangementMode.Single;
    public bool IsVerticalTabArrangement => TabArrangementMode == TabArrangementMode.Vertical;
    public bool IsHorizontalTabArrangement => TabArrangementMode == TabArrangementMode.Horizontal;
    public bool IsTileTabArrangement => TabArrangementMode == TabArrangementMode.Tile;
    public bool IsKeyboardBroadcastBarVisible =>
        HasTabs && !IsTerminalFullScreen && KeyboardBroadcastTarget != KeyboardBroadcastTarget.CurrentSession;
    public bool CanArrangeTabs => Tabs.Count >= 2;
    public bool CanMergeTabGroups => IsTabArrangementEnabled;
    public bool IsSelectedTerminalSession => SelectedTab?.IsTerminalSession == true;
    public bool IsSelectedVncSession => SelectedTab?.IsVncSession == true;
    public bool IsSelectedRdpSession => SelectedTab?.IsRdpSession == true;
    public bool IsSelectedFileTransferSession => SelectedTab?.IsFileTransferSession == true;
    public bool IsSelectedVncToolbarVisible => SelectedTab?.IsVncSession == true &&
                                               SelectedTab.Session.VncShowToolbarButtons;
    public bool IsSelectedRdpToolbarVisible => SelectedTab?.IsRdpSession == true;

    public ServerMonitorViewModel Monitor => SelectedTab?.Monitor ?? _emptyMonitor;
    public ObservableCollection<SessionInfo> QuickSessions => _sessionTreeVm.QuickSessions;
    public string ThemeIcon => IsDarkMode ? "\u263E" : "\u2600";
    public string LanguageIcon => _localization.IsEnglish ? "EN" : "中";
    public string NewSessionText => _localization.Text("Toolbar.New");
    public string NewSessionToolTip => _localization.Text("Toolbar.NewTip");
    public string SessionManagerText => _localization.Text("Toolbar.Sessions");
    public string SessionManagerToolTip => _localization.Text("Toolbar.SessionsTip");
    public string TabBarText => _localization.Text("Toolbar.TabBar");
    public string TabBarToolTip => _localization.Text("Toolbar.TabBarTip");
    public string ConnectText => _localization.Text("Toolbar.Connect");
    public string ConnectToolTip => _localization.Text("Toolbar.ConnectTip");
    public string DisconnectText => _localization.Text("Toolbar.Disconnect");
    public string DisconnectToolTip => _localization.Text("Toolbar.DisconnectTip");
    public string SftpToolTip => _localization.Text("Toolbar.SftpTip");
    public string MonitorText => _localization.Text("Toolbar.Monitor");
    public string MonitorToolTip => _localization.Text("Toolbar.MonitorTip");
    public string TunnelsText => _localization.Text("Toolbar.Tunnels");
    public string TunnelsToolTip => _localization.Text("Toolbar.TunnelsTip");
    public string ThemeToolTip => _localization.Text("Toolbar.ThemeTip");
    public string FullScreenToolTip => _localization.Text("Toolbar.FullScreenTip");
    public string ArrangeText => _localization.Text("Toolbar.Arrange");
    public string ArrangeToolTip => _localization.Text("Toolbar.ArrangeTip");
    public string LanguageToolTip => _localization.Text("Toolbar.LanguageTip");
    public string HelpText => _localization.Text("Toolbar.Help");
    public string HelpToolTip => _localization.Text("Toolbar.HelpTip");
    public string SettingsText => _localization.Text("Toolbar.Settings");
    public string SettingsToolTip => _localization.Text("Toolbar.SettingsTip");
    public string AgentText => _localization.Text("Toolbar.Agent");
    public string AgentToolTip => _localization.Text("Toolbar.AgentTip");
    public bool HasActiveAgentRuns => AgentPanel.HasActiveRuns;
    public string AgentActivityCountText => AgentPanel.ActiveRunCount.ToString();
    public string UpdateText => IsCheckingForUpdates
        ? UpdateProgressText ?? _localization.Text("Toolbar.UpdateChecking")
        : _localization.Text("Toolbar.Update");
    public string UpdateToolTip => _localization.Text("Toolbar.UpdateTip");
    public string AboutCxShellText => _localization.Text("Help.AboutCxShell");
    public string ConnectionAuditText => _localization.Text("Help.ConnectionAudit");
    public string SessionRecordingsText => _localization.Text("Help.SessionRecordings");
    public string ApplicationSettingsText => _localization.Text("Help.ApplicationSettings");
    public string AddQuickSessionToolTip => _localization.Text("Toolbar.AddQuickSessionTip");
    public string ArrangeVerticalText => _localization.Text("Arrange.Vertical");
    public string ArrangeHorizontalText => _localization.Text("Arrange.Horizontal");
    public string ArrangeTileText => _localization.Text("Arrange.Tile");
    public string ArrangeMergeText => _localization.Text("Arrange.Merge");
    public string QuickPropertiesText => _localization.Text("Quick.Properties");
    public string QuickDeleteText => _localization.Text("Quick.Delete");
    public string TabDuplicateText => _localization.Text("TabMenu.Duplicate");
    public string TabCloseText => _localization.Text("TabMenu.Close");
    public string TabPropertiesText => _localization.Text("TabMenu.Properties");
    public string TabAddQuickText => _localization.Text("TabMenu.AddQuick");
    public string TabQuickCommandsText => _localization.Text("TabMenu.QuickCommands");
    public string TabQuickCommandsEmptyText => _localization.Text("TabMenu.QuickCommandsEmpty");
    public string KeyboardBroadcastMenuText => _localization.Text("Terminal.BroadcastMenu");
    public string KeyboardBroadcastCurrentText => _localization.Text("Terminal.Broadcast.Current");
    public string KeyboardBroadcastAllText => _localization.Text("Terminal.Broadcast.All");
    public string KeyboardBroadcastConnectedText => _localization.Text("Terminal.Broadcast.Connected");
    public string KeyboardBroadcastCurrentGroupText => _localization.Text("Terminal.Broadcast.CurrentGroup");
    public string KeyboardBroadcastCloseText => _localization.Text("Terminal.Broadcast.Close");
    public string KeyboardBroadcastReceiveText => _localization.Text("Terminal.Broadcast.Receive");
    public string KeyboardBroadcastStatusText => string.Format(
        _localization.Text("Terminal.Broadcast.Status"),
        GetKeyboardBroadcastTargetText(KeyboardBroadcastTarget),
        ResolveKeyboardBroadcastTargets(SelectedTab).Count);
    public string WelcomeSelectSessionText => _localization.Text("Welcome.SelectSession");
    public string WelcomeBuiltWithAtomUiText => _localization.Text("Welcome.BuiltWithAtomUI");
    public string RecentConnectionsText => _localization.Text("Welcome.RecentConnections");
    public string RecentConnectionsHintText => _localization.Text("Welcome.RecentConnectionsHint");
    public string ViewAllSessionsText => _localization.Text("Welcome.ViewAllSessions");
    public string ConnectRecentSessionText => _localization.Text("Welcome.ConnectRecentSession");
    public string WelcomeNewSessionText => _localization.Text("Welcome.NewSession");
    public string WelcomeSessionManagerText => _localization.Text("Welcome.SessionManager");
    public string FullScreenEscBackText => _localization.Text("FullScreen.EscBack");
    public string ChineseLanguageText => _localization.Text("Language.Chinese");
    public string EnglishLanguageText => _localization.Text("Language.English");

    private Window? _sessionManagerWindow;
    public MainWindowViewModel()
    {
        _sessionTreeVm = new SessionTreeViewModel(this);
        _sessionTree = _sessionTreeVm;
        CommandPalette = new CommandPaletteViewModel(BuildCommandPaletteItems);
        _sftp = _emptySftp;
        _lastSftpPanelWidth = Math.Max(MinimumSftpPanelWidth, _sessionTreeVm.Settings.SftpPanelWidth);
        _isTabBarVisible = _sessionTreeVm.Settings.ShowTabBar;
        _lastAgentPanelWidth = Math.Clamp(
            _sessionTreeVm.Settings.AgentPanelWidth,
            MinimumAgentPanelWidth,
            MaximumAgentPanelWidth);
        _agentPanelWidth = new GridLength(_lastAgentPanelWidth, GridUnitType.Pixel);
        _agentPermissionPolicy = new AgentPermissionPolicy
        {
            AllowCommandExecution = _sessionTreeVm.Settings.AgentAllowCommandExecution,
            PermissionMode = AgentPermissionPolicy.NormalizePermissionMode(
                _sessionTreeVm.Settings.AgentPermissionMode),
            RequireApprovalForDangerousCommands = _sessionTreeVm.Settings.AgentRequireApprovalForDangerousCommands,
            RequireApprovalForChangeCommands = _sessionTreeVm.Settings.AgentRequireApprovalForChangeCommands,
            ReadOnlyMode = _sessionTreeVm.Settings.AgentReadOnlyMode,
            AllowedCommandPrefixes = _sessionTreeVm.Settings.AgentAllowedCommandPrefixes,
            BlockedCommandPrefixes = _sessionTreeVm.Settings.AgentBlockedCommandPrefixes
        };
        AgentSessionGateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(BuildAgentSessionEndpoints),
            _agentPermissionPolicy);
        var agentModelClient = new OpenAiCompatibleAgentModelClient();
        AgentRunCoordinator = new AgentRunCoordinator(
            AgentSessionGateway,
            () => _sessionTreeVm.Settings.AgentProvider,
            agentModelClient,
            new JsonAgentRunHistoryStore());
        AgentRuntimeSessionAdapter = new AgentRuntimeSessionAdapter(
            AgentSessionGateway,
            () => _sessionTreeVm.Settings.AgentProvider,
            agentModelClient,
            AgentRunCoordinator);
        AgentRuntimeHost = new AgentRuntimeHost([(IAgentRuntimeModule)AgentRuntimeSessionAdapter]);
        AgentRuntimeJsonEndpoint = new AgentRuntimeJsonEndpoint(AgentRuntimeHost);
        AgentRuntimeTransport = new InProcessAgentRuntimeTransport(AgentRuntimeHost);
        var runtimeClient = new AgentRuntimeClient(AgentRuntimeTransport);
        AgentRuntimeSession = new AgentRuntimeSession(runtimeClient);
        AgentRuntimeClient = AgentRuntimeSession;
        AgentRuntimeFrameEndpoint = new AgentRuntimeFrameEndpoint(AgentRuntimeHost);
        AgentRuntimeStreamSession = new AgentRuntimeStreamSession(AgentRuntimeFrameEndpoint, AgentRuntimeHost);
        AgentPanel = new AgentPanelViewModel(
            AgentRuntimeClient,
            () => _sessionTreeVm.Settings.AgentProvider);
        AgentPanel.PropertyChanged += OnAgentPanelPropertyChanged;
        _localization.SetLanguage(_sessionTreeVm.Settings.UiLanguage);
        SshHostKeyTrustService.Shared.Configure(_sessionTreeVm.Settings);
        SessionRecordingService.Shared.Configure(_sessionTreeVm.Settings);
        _ = Task.Run(() => SessionRecordingService.Shared.CleanupExpiredAsync());

        Tabs.CollectionChanged += (_, _) =>
        {
            if (Tabs.Count < 2 && TabArrangementMode != TabArrangementMode.Single)
                MergeTabGroups();

            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(CanArrangeTabs));
            NotifyKeyboardBroadcastStateChanged();
            RebuildTileRows();
            OnPropertyChanged(nameof(IsSingleTabContentVisible));
            OnPropertyChanged(nameof(IsArrangedTabsVisible));
            OnPropertyChanged(nameof(IsVerticalTabArrangement));
            OnPropertyChanged(nameof(IsHorizontalTabArrangement));
            OnPropertyChanged(nameof(IsTileTabArrangement));
            if (Tabs.Count == 0)
                RefreshRecentSessions();
            ToggleTerminalFullScreenCommand.NotifyCanExecuteChanged();
            ArrangeTabsVerticalCommand.NotifyCanExecuteChanged();
            ArrangeTabsHorizontalCommand.NotifyCanExecuteChanged();
            ArrangeTabsTileCommand.NotifyCanExecuteChanged();
            MergeTabGroupsCommand.NotifyCanExecuteChanged();
            AgentPanel.RefreshSessions();
        };

        TabGroups.CollectionChanged += (_, _) =>
        {
            RebuildTileRows();
            NotifyKeyboardBroadcastStateChanged();
        };

        // Initialize theme state
        _isDarkMode = Application.Current?.GetThemeManager()?.CurrentTheme?.Appearance == ThemeAppearance.Dark;
        AgentPanel.RefreshSessions();
        RefreshRecentSessions();
    }

    [RelayCommand]
    private async Task ToggleTheme()
    {
        var newMode = IsDarkMode
            ? ApplicationSettings.LightThemeMode
            : ApplicationSettings.DarkThemeMode;
        await ApplyThemeModeAsync(newMode);
    }

    private void ApplyThemeMode(string mode)
    {
        _ = ApplyThemeModeAsync(mode);
    }

    private async Task ApplyThemeModeAsync(string mode)
    {
        var app = Application.Current;
        var themeManager = app?.GetThemeManager();
        if (themeManager == null)
            return;

        var currentTheme = themeManager.CurrentTheme;
        var algorithms = currentTheme?.Algorithms?.ToList() ?? [ThemeAlgorithm.Default];
        var newMode = !string.Equals(mode, ApplicationSettings.LightThemeMode, StringComparison.OrdinalIgnoreCase);
        algorithms.RemoveAll(static algorithm => algorithm == ThemeAlgorithm.Dark);
        if (newMode)
            algorithms.Add(ThemeAlgorithm.Dark);

        var result = await themeManager.ApplyThemeAsync(
            new ThemeRequest(
                currentTheme?.ThemeId ?? IThemeManager.DEFAULT_THEME_ID,
                new ThemeConfigBuilder().WithAlgorithms(algorithms.ToArray()).Build(),
                ThemeTransitionReason.UserRequest));

        if (result.Status == ThemeTransitionStatus.Failed)
            return;

        IsDarkMode = result.State?.Appearance == ThemeAppearance.Dark;
        _sessionTreeVm.Settings.ThemeMode = IsDarkMode
            ? ApplicationSettings.DarkThemeMode
            : ApplicationSettings.LightThemeMode;
        _sessionTreeVm.SaveSettings(_sessionTreeVm.Settings);

        foreach (var tab in Tabs)
        {
            tab.NotifyThemeChanged();
        }
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeIcon));
    }

    partial void OnIsCheckingForUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateText));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    partial void OnUpdateProgressTextChanged(string? value)
    {
        OnPropertyChanged(nameof(UpdateText));
    }

    partial void OnKeyboardBroadcastTargetChanged(KeyboardBroadcastTarget value)
    {
        NotifyKeyboardBroadcastStateChanged();
    }

    private void NotifyKeyboardBroadcastStateChanged()
    {
        foreach (var tab in Tabs)
            UpdateKeyboardBroadcastTabState(tab);

        OnPropertyChanged(nameof(IsKeyboardBroadcastBarVisible));
        OnPropertyChanged(nameof(KeyboardBroadcastStatusText));
    }

    private void UpdateKeyboardBroadcastTabState(TerminalTabViewModel tab)
    {
        var isVisible = tab.IsTerminalSession &&
                        !IsTerminalFullScreen &&
                        KeyboardBroadcastTarget != KeyboardBroadcastTarget.CurrentSession;

        tab.IsKeyboardBroadcastBarVisible = isVisible;
        tab.KeyboardBroadcastStatusText = isVisible
            ? string.Format(
                _localization.Text("Terminal.Broadcast.Status"),
                GetKeyboardBroadcastTargetText(KeyboardBroadcastTarget),
                ResolveKeyboardBroadcastTargets(tab).Count)
            : string.Empty;
        tab.NotifyKeyboardBroadcastLocalizationChanged();
    }

    private string GetKeyboardBroadcastTargetText(KeyboardBroadcastTarget target)
    {
        return target switch
        {
            KeyboardBroadcastTarget.AllSessions => KeyboardBroadcastAllText,
            KeyboardBroadcastTarget.ConnectedSessions => KeyboardBroadcastConnectedText,
            KeyboardBroadcastTarget.CurrentTabGroup => KeyboardBroadcastCurrentGroupText,
            _ => KeyboardBroadcastCurrentText
        };
    }

    [RelayCommand]
    private void SetKeyboardBroadcastTarget(KeyboardBroadcastTarget target)
    {
        KeyboardBroadcastTarget = target;
    }

    public void SendTerminalInput(TerminalViewModel? sourceTerminal, string data)
    {
        if (sourceTerminal == null)
            return;

        var sourceTab = Tabs.FirstOrDefault(tab => ReferenceEquals(tab.Terminal, sourceTerminal));
        if (sourceTab == null)
        {
            sourceTerminal.SendInput(data);
            return;
        }

        SendTerminalInput(sourceTab, data);
    }

    public void SendTerminalInput(TerminalTabViewModel? sourceTab, string data)
    {
        if (sourceTab == null || string.IsNullOrEmpty(data))
            return;

        foreach (var tab in ResolveKeyboardBroadcastTargets(sourceTab))
        {
            try
            {
                tab.Terminal.SendInput(data);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Terminal input send failed for {tab.Session.Name}: {ex.Message}");
            }
        }
    }

    private IReadOnlyList<TerminalTabViewModel> ResolveKeyboardBroadcastTargets(TerminalTabViewModel? sourceTab)
    {
        if (sourceTab?.IsTerminalSession != true)
            return [];

        return KeyboardBroadcastTarget switch
        {
            KeyboardBroadcastTarget.AllSessions => Tabs
                .Where(CanReceiveBroadcastInput)
                .Distinct()
                .ToArray(),
            KeyboardBroadcastTarget.ConnectedSessions => Tabs
                .Where(CanReceiveBroadcastInput)
                .Distinct()
                .ToArray(),
            KeyboardBroadcastTarget.CurrentTabGroup => ResolveCurrentTabGroupTargets(sourceTab),
            _ => CanReceiveTerminalInput(sourceTab)
                ? [sourceTab]
                : []
        };
    }

    private IReadOnlyList<TerminalTabViewModel> ResolveCurrentTabGroupTargets(TerminalTabViewModel sourceTab)
    {
        if (!IsTabArrangementEnabled)
            return CanReceiveTerminalInput(sourceTab) ? [sourceTab] : [];

        var group = FindTabGroup(sourceTab);
        if (group == null)
            return CanReceiveTerminalInput(sourceTab) ? [sourceTab] : [];

        return group.Tabs
            .Where(CanReceiveBroadcastInput)
            .Distinct()
            .ToArray();
    }

    private static bool CanReceiveTerminalInput(TerminalTabViewModel tab)
    {
        return tab.IsTerminalSession && tab.Terminal.IsConnected;
    }

    private static bool CanReceiveBroadcastInput(TerminalTabViewModel tab)
    {
        return CanReceiveTerminalInput(tab) && tab.IsKeyboardBroadcastEnabled;
    }

    [RelayCommand]
    private void SetLanguage(string? language)
    {
        ApplyLanguage(language);
    }

    public void ApplyLanguage(string? language)
    {
        _localization.SetLanguage(language);
        _sessionTreeVm.Settings.UiLanguage = _localization.Language;
        _sessionTreeVm.SaveSettings(_sessionTreeVm.Settings);
        NotifyLocalizationChanged();
    }

    public void StartAutomaticUpdateCheck(string[] startupArgs)
    {
        if (!_sessionTreeVm.Settings.AutoCheckForUpdates)
            return;

        _ = StartAutomaticUpdateCheckAsync(startupArgs);
    }

    public async Task ExecuteCommandLineLaunchAsync(CommandLineLaunchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ErrorMessage))
        {
            ConnectionStatusText = options.ErrorMessage;
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            await ExecuteBastionTokenLaunchAsync(options);
            return;
        }

        if (options.ShowAbout)
            await ShowAbout();

        if (!string.IsNullOrWhiteSpace(options.SavedSessionPath))
        {
            var lookup = _sessionTreeVm.FindSessionByPath(options.SavedSessionPath);
            if (lookup.Status == SessionLookupStatus.NotFound || lookup.Session == null && lookup.Status != SessionLookupStatus.Ambiguous)
            {
                SetCommandLineLaunchError($"Session not found: {options.SavedSessionPath}");
                return;
            }

            if (lookup.Status == SessionLookupStatus.Ambiguous)
            {
                SetCommandLineLaunchError(BuildAmbiguousSessionLaunchMessage(options.SavedSessionPath, lookup.Candidates));
                return;
            }

            var savedSession = lookup.Session!;
            if (options.ShowSessionProperties)
            {
                await EditSessionAsync(savedSession);
                return;
            }

            await ConnectSession(CloneSessionForLaunch(savedSession, options.NewTabName), null, null);
            return;
        }

        if (options.SessionRequest != null)
        {
            var request = options.SessionRequest;
            await ConnectSession(
                request.Session,
                request.ForceAuthPrompt ? null : request.Password,
                request.InitialRemoteDirectory);
            return;
        }

        if (options.OpenSessionManager)
            ShowSessionManager();
    }

    private static string BuildAmbiguousSessionLaunchMessage(
        string sessionPath,
        IReadOnlyList<SessionLookupCandidate> candidates)
    {
        var candidateText = string.Join(
            "; ",
            candidates.Take(6).Select(candidate => $"{candidate.Path} [{candidate.Session.Id}]"));
        var moreText = candidates.Count > 6 ? $" (+{candidates.Count - 6} more)" : string.Empty;
        return $"Session name is ambiguous: {sessionPath}. Use group/session path or session ID. Matches: {candidateText}{moreText}";
    }

    private async Task ExecuteBastionTokenLaunchAsync(CommandLineLaunchOptions options)
    {
        var endpoint = ResolveBastionTokenEndpoint(options.TokenServer);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            SetCommandLineLaunchError("Missing bastion token endpoint. Use -token-server <url> or set Settings.BastionTokenEndpoint.");
            return;
        }

        try
        {
            ConnectionStatusText = "Resolving bastion token...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            var payload = await BastionTokenExchangeService.ExchangeAsync(options.Token!, endpoint);
            var resolvedOptions = CommandLineLaunchOptions.ParseTokenPayload(payload, options);
            await ExecuteCommandLineLaunchAsync(resolvedOptions);
        }
        catch (Exception ex)
        {
            SetCommandLineLaunchError(ex.Message);
        }
    }

    private string ResolveBastionTokenEndpoint(string? commandLineEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(commandLineEndpoint))
            return commandLineEndpoint.Trim();

        if (!string.IsNullOrWhiteSpace(_sessionTreeVm.Settings.BastionTokenEndpoint))
            return _sessionTreeVm.Settings.BastionTokenEndpoint.Trim();

        return Environment.GetEnvironmentVariable("CXSHELL_TOKEN_ENDPOINT")?.Trim() ?? string.Empty;
    }

    private void SetCommandLineLaunchError(string message)
    {
        ConnectionStatusText = message;
        ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
    }

    private static SessionInfo CloneSessionForLaunch(SessionInfo source, string? tabName)
    {
        var clone = new SessionInfo
        {
            Id = source.Id,
            Name = string.IsNullOrWhiteSpace(tabName) ? source.Name : tabName.Trim(),
            GroupId = source.GroupId,
            SortOrder = source.SortOrder,
            CreatedAt = source.CreatedAt
        };
        SessionTreeViewModel.CopySessionValues(clone, source);
        return clone;
    }

    private async Task StartAutomaticUpdateCheckAsync(string[] startupArgs)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await CheckForUpdatesCoreAsync(isManual: false, startupArgs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Automatic update check failed: {ex.Message}");
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdates()
    {
        await CheckForUpdatesCoreAsync(isManual: true, Array.Empty<string>());
    }

    [RelayCommand]
    private async Task ShowAbout()
    {
        var owner = GetMainWindow();
        if (owner == null)
            return;

        await AtomUiDialogService.ShowAboutAsync(
            owner,
            _localization.Text("About.Title"),
            "CxShell",
            string.Format(_localization.Text("About.Version"), BuildAppVersion()),
            _localization.Text("About.Description"),
            _localization.Text("About.BuiltWith"),
            _localization.Text("About.GitHub"),
            "https://github.com/xiaochengzjc/CxShell");
    }

    [RelayCommand]
    private void ShowApplicationSettings()
    {
        ShowSettingsCenter(SettingsSection.Application);
    }

    [RelayCommand]
    private void ShowSessionRecordings()
    {
        ShowSettingsCenter(SettingsSection.SessionRecordings);
    }

    [RelayCommand(CanExecute = nameof(CanShowSshTunnelCenter))]
    private void ShowSshTunnelCenter()
    {
        var owner = GetMainWindow();
        if (owner == null)
            return;

        if (_sshTunnelCenterWindow != null)
        {
            _sshTunnelCenterWindow.Activate();
            return;
        }

        var viewModel = new SshTunnelCenterViewModel(this);
        viewModel.ShowRuleDialogAsync = rule => ShowSshTunnelRuleDialogAsync(owner, rule);
        viewModel.ConfirmDialogAsync = (title, message) =>
            AtomUiDialogService.ShowConfirmAsync(owner, title, message);
        _sshTunnelCenterWindow = new SshTunnelCenterWindow(viewModel);
        _sshTunnelCenterWindow.Closed += (_, _) => _sshTunnelCenterWindow = null;
        _sshTunnelCenterWindow.Show(owner);
    }

    private bool CanShowSshTunnelCenter()
        => SelectedTab is { IsTerminalSession: true, Session.Protocol: SessionProtocol.SSH };

    private static async Task<SshTunnelRule?> ShowSshTunnelRuleDialogAsync(
        AtomUI.Desktop.Controls.Window owner,
        SshTunnelRule? source)
    {
        var dialog = new SshTunnelRuleDialogWindow(new SshTunnelRuleDialogViewModel(source));
        return await dialog.ShowRuleDialogAsync(owner);
    }

    private void ApplyApplicationSettings(ApplicationSettings settings)
    {
        if (!ReferenceEquals(_sessionTreeVm.Settings, settings))
            return;

        _sessionTreeVm.SaveSettings(settings);
        _agentPermissionPolicy.AllowCommandExecution = settings.AgentAllowCommandExecution;
        _agentPermissionPolicy.PermissionMode = AgentPermissionPolicy.NormalizePermissionMode(
            settings.AgentPermissionMode);
        _agentPermissionPolicy.RequireApprovalForDangerousCommands = settings.AgentRequireApprovalForDangerousCommands;
        _agentPermissionPolicy.RequireApprovalForChangeCommands = settings.AgentRequireApprovalForChangeCommands;
        _agentPermissionPolicy.ReadOnlyMode = settings.AgentReadOnlyMode;
        _agentPermissionPolicy.AllowedCommandPrefixes = settings.AgentAllowedCommandPrefixes;
        _agentPermissionPolicy.BlockedCommandPrefixes = settings.AgentBlockedCommandPrefixes;
        AgentPanel.RefreshProviderStatus();
        SshHostKeyTrustService.Shared.Configure(settings);
        SessionRecordingService.Shared.Configure(settings);
        foreach (var tab in Tabs.Where(item => item.IsTerminalSession))
        {
            tab.Terminal.SetCommandSuggestionsEnabled(settings.EnableCommandSuggestions);
            tab.Terminal.RefreshRecordingOptions();
        }
        if (IsTabBarVisible != settings.ShowTabBar)
            IsTabBarVisible = settings.ShowTabBar;
    }

    private static string BuildAppVersion()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Split('+')[0];

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private async Task CheckForUpdatesCoreAsync(bool isManual, string[] restartArgs)
    {
        if (IsCheckingForUpdates)
            return;

        var owner = GetActiveWindow();
        IsCheckingForUpdates = true;
        UpdateProgressText = _localization.Text("Toolbar.UpdateChecking");

        try
        {
            var result = await _appUpdateService.CheckForUpdatesAsync(_sessionTreeVm.Settings.IncludePrereleaseUpdates);
            switch (result.Status)
            {
                case AppUpdateCheckStatus.NotInstalled:
                    if (isManual && owner != null)
                    {
                        await AtomUiDialogService.ShowMessageAsync(
                            owner,
                            _localization.Text("Update.Title"),
                            _localization.Text("Update.NotInstalled"),
                            AtomUI.Desktop.Controls.MessageBoxStyle.Warning);
                    }
                    break;

                case AppUpdateCheckStatus.NoUpdate:
                    if (isManual && owner != null)
                    {
                        await AtomUiDialogService.ShowMessageAsync(
                            owner,
                            _localization.Text("Update.Title"),
                            _localization.Text("Update.NoUpdate"),
                            AtomUI.Desktop.Controls.MessageBoxStyle.Success);
                    }
                    break;

                case AppUpdateCheckStatus.PendingRestart:
                    if (result.Update != null)
                        await PromptRestartForUpdateAsync(result.Update, restartArgs);
                    break;

                case AppUpdateCheckStatus.UpdateAvailable:
                    if (result.Update != null)
                        await PromptDownloadUpdateAsync(result.Update, restartArgs);
                    break;

                case AppUpdateCheckStatus.Failed:
                    if (isManual && owner != null)
                    {
                        await AtomUiDialogService.ShowMessageAsync(
                            owner,
                            _localization.Text("Update.Title"),
                            string.Format(_localization.Text("Update.Failed"), BuildUpdateErrorMessage(result.ErrorMessage)),
                            AtomUI.Desktop.Controls.MessageBoxStyle.Error);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Automatic update check failed: {result.ErrorMessage}");
                    }
                    break;
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
            UpdateProgressText = null;
        }
    }

    private string BuildUpdateErrorMessage(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return "unknown";

        if (errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return _localization.Text("Update.RateLimited");
        }

        if (errorMessage.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("additional bytes expected", StringComparison.OrdinalIgnoreCase))
        {
            return _localization.Text("Update.NetworkInterrupted");
        }

        return errorMessage;
    }

    private async Task PromptDownloadUpdateAsync(AppUpdateHandle update, string[] restartArgs)
    {
        var owner = GetActiveWindow();
        if (owner == null)
            return;

        var message = BuildUpdateAvailableMessage(update);
        var shouldDownload = await AtomUiDialogService.ShowConfirmAsync(
            owner,
            _localization.Text("Update.Title"),
            message);
        if (!shouldDownload)
            return;

        using var downloadCts = new CancellationTokenSource();
        var progressWindow = ShowUpdateProgressWindow(owner, update, downloadCts);
        UpdateProgressText = _localization.Text("Update.Downloading");

        try
        {
            await _appUpdateService.DownloadUpdatesAsync(update, progress =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgressText = string.Format(_localization.Text("Toolbar.UpdateDownloading"), progress);
                    if (_updateProgressViewModel != null)
                    {
                        _updateProgressViewModel.IsIndeterminate = false;
                        _updateProgressViewModel.Progress = Math.Clamp(progress, 0, 100);
                        _updateProgressViewModel.StatusText = UpdateProgressText;
                    }
                });
            }, downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await AtomUiDialogService.ShowMessageAsync(
                owner,
                _localization.Text("Update.Title"),
                string.Format(_localization.Text("Update.DownloadFailed"), BuildUpdateErrorMessage(ex.Message)),
                AtomUI.Desktop.Controls.MessageBoxStyle.Error);
            return;
        }
        finally
        {
            CloseUpdateProgressWindow(progressWindow);
        }

        await PromptRestartForUpdateAsync(update, restartArgs);
    }

    private UpdateProgressWindow ShowUpdateProgressWindow(
        TopLevel owner,
        AppUpdateHandle update,
        CancellationTokenSource downloadCts)
    {
        CloseUpdateProgressWindow(_updateProgressWindow);

        _updateProgressViewModel = new UpdateProgressViewModel
        {
            Title = _localization.Text("Update.Title"),
            StatusText = _localization.Text("Update.Downloading"),
            VersionText = string.Format(_localization.Text("Update.ProgressVersion"), update.CurrentVersion, update.TargetVersion),
            BackgroundText = _localization.Text("Update.Background"),
            CancelText = _localization.Text("Update.Cancel")
        };

        var window = new UpdateProgressWindow
        {
            DataContext = _updateProgressViewModel
        };
        window.CancelRequested += (_, _) => downloadCts.Cancel();
        _updateProgressWindow = window;

        if (owner is Window ownerWindow)
            window.Show(ownerWindow);
        else
            window.Show();

        return window;
    }

    private void CloseUpdateProgressWindow(UpdateProgressWindow? window)
    {
        if (window == null)
            return;

        try
        {
            window.CloseForCompletion();
        }
        catch
        {
            // Ignore close failures during shutdown or update restart.
        }
        finally
        {
            if (_updateProgressWindow == window)
            {
                _updateProgressWindow = null;
                _updateProgressViewModel = null;
            }
        }
    }

    private async Task PromptRestartForUpdateAsync(AppUpdateHandle update, string[] restartArgs)
    {
        var owner = GetActiveWindow();
        if (owner == null)
            return;

        var restart = await AtomUiDialogService.ShowConfirmAsync(
            owner,
            _localization.Text("Update.Title"),
            AppendMacInstallPermissionWarning(
                string.Format(_localization.Text("Update.DownloadedMessage"), update.TargetVersion)));
        if (!restart)
            return;

        PrepareForUpdateRestart();
        _appUpdateService.ApplyUpdatesAndRestart(update, restartArgs);
    }

    private string BuildUpdateAvailableMessage(AppUpdateHandle update)
    {
        var message = string.Format(
            _localization.Text("Update.AvailableMessage"),
            update.TargetVersion,
            update.CurrentVersion);

        var notes = BuildReleaseNotesPreview(update.ReleaseNotes);
        if (!string.IsNullOrWhiteSpace(notes))
            message += Environment.NewLine + Environment.NewLine + _localization.Text("Update.ReleaseNotes") + Environment.NewLine + notes;

        return AppendMacInstallPermissionWarning(message);
    }

    private string AppendMacInstallPermissionWarning(string message)
    {
        var permissionInfo = _appUpdateService.GetMacInstallPermissionInfo();
        if (!permissionInfo.MayRequireAdminPassword)
            return message;

        return message +
               Environment.NewLine +
               Environment.NewLine +
               string.Format(
                   _localization.Text("Update.MacApplicationsWarning"),
                   permissionInfo.AppBundlePath,
                   permissionInfo.RecommendedUserApplicationsPath);
    }

    private static string BuildReleaseNotesPreview(string? releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
            return string.Empty;

        var normalized = releaseNotes.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return normalized.Length <= 700
            ? normalized
            : normalized[..700] + "...";
    }

    private void PrepareForUpdateRestart()
    {
        foreach (var tab in Tabs.ToList())
            CloseTab(tab);

        StopAllMonitors();
    }

    private void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(LanguageIcon));
        OnPropertyChanged(nameof(NewSessionText));
        OnPropertyChanged(nameof(NewSessionToolTip));
        OnPropertyChanged(nameof(SessionManagerText));
        OnPropertyChanged(nameof(SessionManagerToolTip));
        OnPropertyChanged(nameof(TabBarText));
        OnPropertyChanged(nameof(TabBarToolTip));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(ConnectToolTip));
        OnPropertyChanged(nameof(DisconnectText));
        OnPropertyChanged(nameof(DisconnectToolTip));
        OnPropertyChanged(nameof(SftpToolTip));
        OnPropertyChanged(nameof(MonitorText));
        OnPropertyChanged(nameof(MonitorToolTip));
        OnPropertyChanged(nameof(TunnelsText));
        OnPropertyChanged(nameof(TunnelsToolTip));
        OnPropertyChanged(nameof(ThemeToolTip));
        OnPropertyChanged(nameof(FullScreenToolTip));
        OnPropertyChanged(nameof(ArrangeText));
        OnPropertyChanged(nameof(ArrangeToolTip));
        OnPropertyChanged(nameof(LanguageToolTip));
        OnPropertyChanged(nameof(HelpText));
        OnPropertyChanged(nameof(HelpToolTip));
        OnPropertyChanged(nameof(SettingsText));
        OnPropertyChanged(nameof(SettingsToolTip));
        OnPropertyChanged(nameof(AgentText));
        OnPropertyChanged(nameof(AgentToolTip));
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(UpdateToolTip));
        OnPropertyChanged(nameof(AboutCxShellText));
        OnPropertyChanged(nameof(ConnectionAuditText));
        OnPropertyChanged(nameof(SessionRecordingsText));
        OnPropertyChanged(nameof(ApplicationSettingsText));
        OnPropertyChanged(nameof(AddQuickSessionToolTip));
        OnPropertyChanged(nameof(ArrangeVerticalText));
        OnPropertyChanged(nameof(ArrangeHorizontalText));
        OnPropertyChanged(nameof(ArrangeTileText));
        OnPropertyChanged(nameof(ArrangeMergeText));
        OnPropertyChanged(nameof(QuickPropertiesText));
        OnPropertyChanged(nameof(QuickDeleteText));
        OnPropertyChanged(nameof(TabDuplicateText));
        OnPropertyChanged(nameof(TabCloseText));
        OnPropertyChanged(nameof(TabPropertiesText));
        OnPropertyChanged(nameof(TabAddQuickText));
        OnPropertyChanged(nameof(TabQuickCommandsText));
        OnPropertyChanged(nameof(TabQuickCommandsEmptyText));
        OnPropertyChanged(nameof(KeyboardBroadcastMenuText));
        OnPropertyChanged(nameof(KeyboardBroadcastCurrentText));
        OnPropertyChanged(nameof(KeyboardBroadcastAllText));
        OnPropertyChanged(nameof(KeyboardBroadcastConnectedText));
        OnPropertyChanged(nameof(KeyboardBroadcastCurrentGroupText));
        OnPropertyChanged(nameof(KeyboardBroadcastCloseText));
        OnPropertyChanged(nameof(KeyboardBroadcastReceiveText));
        OnPropertyChanged(nameof(KeyboardBroadcastStatusText));
        NotifyKeyboardBroadcastStateChanged();
        OnPropertyChanged(nameof(WelcomeSelectSessionText));
        OnPropertyChanged(nameof(WelcomeBuiltWithAtomUiText));
        OnPropertyChanged(nameof(RecentConnectionsText));
        OnPropertyChanged(nameof(RecentConnectionsHintText));
        OnPropertyChanged(nameof(ViewAllSessionsText));
        OnPropertyChanged(nameof(ConnectRecentSessionText));
        OnPropertyChanged(nameof(WelcomeNewSessionText));
        OnPropertyChanged(nameof(WelcomeSessionManagerText));
        RefreshRecentSessions();
        OnPropertyChanged(nameof(FullScreenEscBackText));
        OnPropertyChanged(nameof(ChineseLanguageText));
        OnPropertyChanged(nameof(EnglishLanguageText));
        AgentPanel.NotifyLocalizationChanged();
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsSelected = tab == value;

        ActivateTabGroupForSelectedTab(value);
        NotifyKeyboardBroadcastStateChanged();

        NotifySelectedContentVisibilityChanged();
        OnPropertyChanged(nameof(Monitor));
        if (value?.Vnc != null)
        {
            if (IsSftpVisible)
                IsSftpVisible = false;
            if (IsMonitorVisible)
                IsMonitorVisible = false;
        }
        if (value?.Rdp != null)
        {
            if (IsSftpVisible)
                IsSftpVisible = false;
            if (IsMonitorVisible)
                IsMonitorVisible = false;
        }
        if (value?.FileTransfer != null)
        {
            if (IsSftpVisible)
                IsSftpVisible = false;
            if (IsMonitorVisible)
                IsMonitorVisible = false;
        }
        UpdateStatusBar();
        UpdateTerminalSize();
        UpdateMonitor(value);
        UpdateSftp(value);
        CurrentConnectCommand.NotifyCanExecuteChanged();
        CurrentDisconnectCommand.NotifyCanExecuteChanged();
        AddCurrentSessionToQuickBarCommand.NotifyCanExecuteChanged();
        ToggleTerminalFullScreenCommand.NotifyCanExecuteChanged();
        ShowSshTunnelCenterCommand.NotifyCanExecuteChanged();
    }

    partial void OnTabArrangementModeChanged(TabArrangementMode value)
    {
        OnPropertyChanged(nameof(IsTabArrangementEnabled));
        OnPropertyChanged(nameof(CanMergeTabGroups));
        OnPropertyChanged(nameof(IsMainTabHeaderVisible));
        OnPropertyChanged(nameof(IsSingleTabContentVisible));
        OnPropertyChanged(nameof(IsArrangedTabsVisible));
        OnPropertyChanged(nameof(IsVerticalTabArrangement));
        OnPropertyChanged(nameof(IsHorizontalTabArrangement));
        OnPropertyChanged(nameof(IsTileTabArrangement));
        RebuildTileRows();
        NotifyKeyboardBroadcastStateChanged();
        MergeTabGroupsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTabGroupChanged(TerminalTabGroupViewModel? value)
    {
        NotifyKeyboardBroadcastStateChanged();
    }

    private void NotifySelectedContentVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsSelectedTerminalSession));
        OnPropertyChanged(nameof(IsSelectedVncSession));
        OnPropertyChanged(nameof(IsSelectedRdpSession));
        OnPropertyChanged(nameof(IsSelectedFileTransferSession));
        OnPropertyChanged(nameof(IsSelectedVncToolbarVisible));
        OnPropertyChanged(nameof(IsSelectedRdpToolbarVisible));
    }

    partial void OnIsMonitorVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMonitorPanelVisible));
        OnPropertyChanged(nameof(IsAgentPanelHostVisible));
        OnPropertyChanged(nameof(AgentSplitterWidth));
        OnPropertyChanged(nameof(AgentPanelColumnWidth));
        if (value)
        {
            OnPropertyChanged(nameof(Monitor));
            UpdateMonitor(SelectedTab);
        }
        else
        {
            StopAllMonitors();
        }
    }

    partial void OnIsSftpVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSftpPanelVisible));
        OnPropertyChanged(nameof(SftpSplitterWidth));
        if (value)
        {
            if (!IsTerminalFullScreen)
                RestoreSftpPanelWidth();
            UpdateSftp(SelectedTab);
        }
        else
        {
            CollapseSftpPanelWidth();
        }
    }

    partial void OnIsTerminalFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMainChromeVisible));
        OnPropertyChanged(nameof(IsQuickSessionBarVisible));
        OnPropertyChanged(nameof(IsSftpPanelVisible));
        OnPropertyChanged(nameof(SftpSplitterWidth));
        OnPropertyChanged(nameof(IsMonitorPanelVisible));
        OnPropertyChanged(nameof(IsAgentPanelHostVisible));
        OnPropertyChanged(nameof(AgentSplitterWidth));
        OnPropertyChanged(nameof(IsTabHeaderVisible));
        OnPropertyChanged(nameof(IsMainTabHeaderVisible));
        OnPropertyChanged(nameof(IsSingleTabContentVisible));
        OnPropertyChanged(nameof(IsArrangedTabsVisible));
        NotifyKeyboardBroadcastStateChanged();
        if (value)
            CollapseSftpPanelWidth();
        else if (IsSftpVisible)
            RestoreSftpPanelWidth();
        IsFullScreenHintVisible = value;
    }

    partial void OnIsAgentPanelVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentPanelHostVisible));
        OnPropertyChanged(nameof(AgentSplitterWidth));
        OnPropertyChanged(nameof(AgentPanelColumnWidth));
        if (value)
        {
            AgentPanel.RefreshSessions();
            AgentPanel.RefreshRunHistory();
            AgentPanel.EnsureSessionSelection(SelectedTab?.Session.Id);
            AgentPanel.RefreshActiveRun();
        }
    }

    partial void OnSftpPanelWidthChanged(GridLength value)
    {
        OnPropertyChanged(nameof(SftpPanelPixelWidth));

        if (value.GridUnitType != GridUnitType.Pixel || value.Value <= 0)
            return;

        var clamped = Math.Max(MinimumSftpPanelWidth, value.Value);
        _lastSftpPanelWidth = clamped;

        if (Math.Abs(clamped - value.Value) > 0.5)
            SftpPanelWidth = new GridLength(clamped);
    }

    public void PersistSftpPanelWidth()
    {
        var width = Math.Max(MinimumSftpPanelWidth, _lastSftpPanelWidth);
        if (Math.Abs(_sessionTreeVm.Settings.SftpPanelWidth - width) < 0.5)
            return;

        _sessionTreeVm.Settings.SftpPanelWidth = width;
        _sessionTreeVm.SaveSettings(_sessionTreeVm.Settings);
    }

    partial void OnAgentPanelWidthChanged(GridLength value)
    {
        if (value.GridUnitType != GridUnitType.Pixel || value.Value <= 0)
            return;

        var clamped = Math.Clamp(value.Value, MinimumAgentPanelWidth, MaximumAgentPanelWidth);
        _lastAgentPanelWidth = clamped;

        if (Math.Abs(clamped - value.Value) > 0.5)
            AgentPanelWidth = new GridLength(clamped, GridUnitType.Pixel);
    }

    public void PersistAgentPanelWidth()
    {
        var width = Math.Clamp(_lastAgentPanelWidth, MinimumAgentPanelWidth, MaximumAgentPanelWidth);
        if (Math.Abs(_sessionTreeVm.Settings.AgentPanelWidth - width) < 0.5)
            return;

        _sessionTreeVm.Settings.AgentPanelWidth = width;
        _sessionTreeVm.SaveSettings(_sessionTreeVm.Settings);
    }

    private bool CanArrangeTabsCore() => CanArrangeTabs;

    [RelayCommand(CanExecute = nameof(CanArrangeTabsCore))]
    private void ArrangeTabsVertical()
    {
        if (!IsTabArrangementEnabled || TabGroups.Count == 0)
            BuildTabGroupsFromTabs();

        TabArrangementMode = TabArrangementMode.Vertical;
        ActivateTabGroupForSelectedTab(SelectedTab);
    }

    [RelayCommand(CanExecute = nameof(CanArrangeTabsCore))]
    private void ArrangeTabsHorizontal()
    {
        if (!IsTabArrangementEnabled || TabGroups.Count == 0)
            BuildTabGroupsFromTabs();

        TabArrangementMode = TabArrangementMode.Horizontal;
        ActivateTabGroupForSelectedTab(SelectedTab);
    }

    [RelayCommand(CanExecute = nameof(CanArrangeTabsCore))]
    private void ArrangeTabsTile()
    {
        if (!IsTabArrangementEnabled || TabGroups.Count == 0)
            BuildTabGroupsFromTabs();

        TabArrangementMode = TabArrangementMode.Tile;
        ActivateTabGroupForSelectedTab(SelectedTab);
    }

    private bool CanMergeTabGroupsCore() => CanMergeTabGroups;

    [RelayCommand(CanExecute = nameof(CanMergeTabGroupsCore))]
    private void MergeTabGroups()
    {
        TabArrangementMode = TabArrangementMode.Single;
        TabGroups.Clear();
        TileRows.Clear();
        SetSelectedTabGroup(null);
    }

    private void UpdateMonitor(TerminalTabViewModel? tab)
    {
        if (!IsMonitorVisible || _isApplicationSuspended)
            return;
        if (tab == null ||
            tab.IsDisposed ||
            !Tabs.Contains(tab) ||
            tab.Vnc != null ||
            tab.Rdp != null ||
            !tab.Terminal.IsConnected ||
            tab.Session.Protocol != SessionProtocol.SSH)
        {
            return;
        }

        if (!tab.Session.SshEnableServerMonitoring)
        {
            tab.Monitor.StopMonitoring();
            return;
        }

        var isWindowsOpenSsh = !tab.Terminal.SupportsPosixShellFeatures;
        tab.Monitor.SwitchConnection(
            tab.Session,
            tab.ConnectedPassword,
            isWindowsOpenSsh ? null : tab.Terminal.RunRemoteCommandAsync,
            isWindowsOpenSsh,
            tab.Session.SshMonitorRefreshIntervalSeconds,
            tab.Session.SshEnableMonitorNetworkLatency);
    }

    private void StopAllMonitors()
    {
        _emptyMonitor.StopMonitoring();
        foreach (var tab in Tabs)
            tab.Monitor.StopMonitoring();
    }

    public void SetApplicationSuspended(bool suspended)
    {
        if (_isApplicationSuspended == suspended)
            return;

        _isApplicationSuspended = suspended;
        foreach (var tab in Tabs)
            tab.Monitor.SetSuspended(suspended);

        if (!suspended)
            UpdateMonitor(SelectedTab);
    }

    private async Task UpdateCompanionPanelsAfterTerminalConnectAsync(TerminalTabViewModel tab)
    {
        if (tab.IsDisposed || !Tabs.Contains(tab))
            return;

        if (tab.Session.Protocol != SessionProtocol.SSH)
        {
            UpdateMonitor(tab);
            UpdateSftp(tab);
            return;
        }

        var isWindowsOpenSsh = !tab.Terminal.SupportsPosixShellFeatures;

        if (tab.Session.SshAutoOpenMonitorPanel &&
            tab.Session.SshEnableServerMonitoring)
        {
            if (!IsMonitorVisible)
                IsMonitorVisible = true;
            else
                UpdateMonitor(tab);
        }
        else if (IsMonitorVisible)
        {
            UpdateMonitor(tab);
        }

        if (tab.Session.SshAutoOpenSftpPanel &&
            isWindowsOpenSsh)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2500), tab.LifetimeToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (tab.IsDisposed ||
                !Tabs.Contains(tab) ||
                SelectedTab != tab ||
                !tab.Terminal.IsConnected)
                return;
        }

        if (tab.Session.SshAutoOpenSftpPanel)
        {
            if (!IsSftpVisible)
                IsSftpVisible = true;
            else
                UpdateSftp(tab);
        }
        else if (IsSftpVisible)
        {
            UpdateSftp(tab);
        }

    }

    private void UpdateSftp(TerminalTabViewModel? tab)
    {
        if (!IsSftpVisible) return;
        if (tab == null ||
            tab.IsDisposed ||
            !Tabs.Contains(tab) ||
            tab.Vnc != null ||
            tab.Rdp != null ||
            tab.FileTransfer != null ||
            !tab.Terminal.IsConnected ||
            tab.Session.Protocol != SessionProtocol.SSH)
        {
            if (!ReferenceEquals(Sftp, _emptySftp))
                Sftp = _emptySftp;
            return;
        }

        var target = tab.CompanionSftp;
        if (!ReferenceEquals(Sftp, target))
            Sftp = target;

        if (!target.IsBrowsingSession(tab.Session) && !target.IsLoading)
            target.SwitchConnection(tab.Session, tab.ConnectedPassword);
    }

    private double _lastSftpPanelWidth = DefaultSftpPanelWidth;
    private double _lastAgentPanelWidth = DefaultAgentPanelWidth;

    private void CollapseSftpPanelWidth()
    {
        if (SftpPanelWidth.Value <= 0)
            return;

        _lastSftpPanelWidth = Math.Max(MinimumSftpPanelWidth, SftpPanelWidth.Value);
        SftpPanelWidth = new GridLength(0);
    }

    private void RestoreSftpPanelWidth(bool resetToDefault = false)
    {
        if (SftpPanelWidth.Value > 0)
        {
            if (resetToDefault)
                SftpPanelWidth = new GridLength(DefaultSftpPanelWidth);
            return;
        }

        if (resetToDefault)
            _lastSftpPanelWidth = DefaultSftpPanelWidth;
        SftpPanelWidth = new GridLength(Math.Max(MinimumSftpPanelWidth, _lastSftpPanelWidth));
    }

    [RelayCommand]
    private void ShowSessionManager()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.MainWindow;
        if (owner == null) return;

        if (_sessionManagerWindow != null)
        {
            _sessionManagerWindow.Activate();
            return;
        }

        _sessionManagerWindow = new SessionManagerWindow(_sessionTreeVm)
        {
            ShowInTaskbar = false
        };
        _sessionManagerWindow.Closed += (_, _) =>
        {
            _sessionManagerWindow = null;
            RefreshRecentSessions();
        };
        _sessionManagerWindow.Show(owner);
    }

    public void ShowSessionManagerOnStartupIfEnabled()
    {
        if (_sessionTreeVm.Settings.ShowSessionManagerOnStartup)
            ShowSessionManager();
    }

    public void OpenCommandPalette()
    {
        CommandPalette.Open();
    }

    [RelayCommand]
    private Task ShowConnectionAudit()
    {
        return ShowConnectionAuditAsync();
    }

    [RelayCommand]
    private void ShowRecentConnections()
    {
        var owner = GetMainWindow();
        if (owner == null)
            return;

        if (_recentConnectionsWindow != null)
        {
            _recentConnectionsWindow.Activate();
            return;
        }

        var viewModel = new RecentConnectionsViewModel(
            _connectionAuditService,
            _sessionTreeVm.GetAllSessions,
            ConnectFromRecentConnectionsAsync);
        _recentConnectionsWindow = new RecentConnectionsWindow
        {
            DataContext = viewModel,
            ShowInTaskbar = false
        };
        _recentConnectionsWindow.Closed += (_, _) => _recentConnectionsWindow = null;
        _recentConnectionsWindow.Show(owner);
    }

    private async Task ConnectFromRecentConnectionsAsync(SessionInfo session)
    {
        var window = _recentConnectionsWindow;
        _recentConnectionsWindow = null;
        window?.Close();
        await ConnectSession(session);
    }

    private IReadOnlyList<CommandPaletteItem> BuildCommandPaletteItems()
    {
        var items = new List<CommandPaletteItem>();
        var recentSessionsCategory = _localization.Text("Palette.RecentSessions");
        var sessionsCategory = _localization.Text("Palette.Sessions");
        var commandsCategory = _localization.Text("Palette.Commands");
        var quickCommandsCategory = _localization.Text("Palette.QuickCommands");
        var connectHint = _localization.Text("Palette.ConnectHint");
        var executeHint = _localization.Text("Palette.ExecuteHint");
        var sessions = _sessionTreeVm.GetAllSessions();
        var sessionsById = sessions.ToDictionary(session => session.Id);
        var recentSessionIds = new HashSet<Guid>();

        foreach (var entry in _connectionAuditService.ReadRecentSuccessfulConnections())
        {
            if (!sessionsById.TryGetValue(entry.SessionId, out var recentSession))
                continue;

            recentSessionIds.Add(recentSession.Id);
            AddSessionPaletteItem(
                items,
                recentSessionsCategory,
                recentSession,
                $"{connectHint} · {entry.LocalTimestamp:MM-dd HH:mm}");
        }

        foreach (var session in sessions.Where(session => !recentSessionIds.Contains(session.Id)))
        {
            AddSessionPaletteItem(items, sessionsCategory, session, connectHint);
        }

        var selectedTab = SelectedTab;
        if (selectedTab?.IsTerminalSession == true && selectedTab.Terminal.IsConnected)
        {
            foreach (var quickCommand in GetQuickCommands(selectedTab))
            {
                var capturedCommand = quickCommand;
                items.Add(new CommandPaletteItem(
                    quickCommandsCategory,
                    quickCommand.Name,
                    () => ExecuteQuickCommand(selectedTab, capturedCommand),
                    $"{executeHint} · {quickCommand.CommandText}"));
            }
        }

        AddCommand(items, commandsCategory, NewSessionText, "Ctrl+N", NewSessionCommand);
        AddCommand(items, commandsCategory, SessionManagerText, null, ShowSessionManagerCommand);
        AddCommand(items, commandsCategory, SftpToolTip, null, ToggleSftpCommand);
        AddCommand(items, commandsCategory, MonitorText, null, ToggleMonitorCommand);
        AddCommand(items, commandsCategory, TabBarText, null, ToggleTabBarCommand);
        AddCommand(items, commandsCategory, _localization.Text("Palette.ToggleTheme"), null, ToggleThemeCommand);
        AddCommand(items, commandsCategory, FullScreenToolTip, "Esc", ToggleTerminalFullScreenCommand);
        AddCommand(items, commandsCategory, ArrangeVerticalText, null, ArrangeTabsVerticalCommand);
        AddCommand(items, commandsCategory, ArrangeHorizontalText, null, ArrangeTabsHorizontalCommand);
        AddCommand(items, commandsCategory, ArrangeTileText, null, ArrangeTabsTileCommand);
        AddCommand(items, commandsCategory, ArrangeMergeText, null, MergeTabGroupsCommand);
        AddCommand(items, commandsCategory, AboutCxShellText, null, ShowAboutCommand);
        AddCommand(items, commandsCategory, ConnectionAuditText, null, ShowConnectionAuditCommand);
        AddCommand(items, commandsCategory, SessionRecordingsText, null, ShowSessionRecordingsCommand);
        AddCommand(items, commandsCategory, ApplicationSettingsText, null, ShowApplicationSettingsCommand);
        AddCommand(items, commandsCategory, _localization.Text("Toolbar.Update"), null, CheckForUpdatesCommand);
        return items;
    }

    private void AddSessionPaletteItem(
        ICollection<CommandPaletteItem> items,
        string category,
        SessionInfo session,
        string connectHint)
    {
        var capturedSession = session;
        var title = string.IsNullOrWhiteSpace(session.Name) ? session.Host : session.Name;
        var endpoint = string.IsNullOrWhiteSpace(session.Username)
            ? $"{session.Host}:{session.Port}"
            : $"{session.Username}@{session.Host}:{session.Port}";
        items.Add(new CommandPaletteItem(
            category,
            title,
            () => _ = ConnectSession(capturedSession),
            $"{connectHint} · {endpoint}",
            session.Protocol.ToString(),
            isSession: true));
    }

    private static void AddCommand(
        ICollection<CommandPaletteItem> items,
        string category,
        string title,
        string? hint,
        ICommand command)
    {
        if (!command.CanExecute(null))
            return;

        items.Add(new CommandPaletteItem(category, title, () => command.Execute(null), hint));
    }

    [RelayCommand]
    private void ToggleMonitor()
    {
        IsMonitorVisible = !IsMonitorVisible;
    }

    [RelayCommand]
    private void ToggleSftp()
    {
        IsSftpVisible = !IsSftpVisible;
    }

    [RelayCommand]
    private void ToggleTabBar()
    {
        IsTabBarVisible = !IsTabBarVisible;
    }

    [RelayCommand]
    private void ToggleAgentPanel()
    {
        ToggleAgentPanelVisibility();
    }

    private void OnAgentPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentPanelViewModel.ActiveRunCount) or
            nameof(AgentPanelViewModel.HasActiveRuns))
        {
            OnPropertyChanged(nameof(HasActiveAgentRuns));
            OnPropertyChanged(nameof(AgentActivityCountText));
        }
    }

    public void ToggleAgentPanelVisibility()
    {
        IsAgentPanelVisible = !IsAgentPanelVisible;
    }

    partial void OnIsTabBarVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsQuickSessionBarVisible));
        _sessionTreeVm.Settings.ShowTabBar = value;
        _sessionTreeVm.SaveSettings(_sessionTreeVm.Settings);
    }

    private bool CanToggleTerminalFullScreen()
    {
        return SelectedTab != null;
    }

    [RelayCommand(CanExecute = nameof(CanToggleTerminalFullScreen))]
    private void ToggleTerminalFullScreen()
    {
        IsTerminalFullScreen = !IsTerminalFullScreen;
    }

    public void ExitTerminalFullScreen()
    {
        IsTerminalFullScreen = false;
    }

    private TerminalTabGroupViewModel? FindTabGroup(TerminalTabViewModel tab)
    {
        return TabGroups.FirstOrDefault(group => group.Tabs.Contains(tab));
    }

    private void ActivateTabGroupForSelectedTab(TerminalTabViewModel? tab)
    {
        if (!IsTabArrangementEnabled || tab == null)
        {
            SetSelectedTabGroup(null);
            return;
        }

        var group = FindTabGroup(tab);
        if (group == null)
            return;

        group.SelectedTab = tab;
        SetSelectedTabGroup(group);
    }

    private void SetSelectedTabGroup(TerminalTabGroupViewModel? group)
    {
        foreach (var tabGroup in TabGroups)
            tabGroup.IsSelected = tabGroup == group;

        if (SelectedTabGroup != group)
            SelectedTabGroup = group;
    }

    private void BuildTabGroupsFromTabs()
    {
        TabGroups.Clear();

        foreach (var tab in Tabs)
            TabGroups.Add(new TerminalTabGroupViewModel(tab));

        RebuildTileRows();
        ActivateTabGroupForSelectedTab(SelectedTab ?? Tabs.LastOrDefault());
    }

    private void RebuildTileRows()
    {
        TileRows.Clear();
        if (!IsTileTabArrangement || TabGroups.Count == 0)
            return;

        foreach (var row in BuildTileRows(TabGroups.ToArray()))
            TileRows.Add(row);
    }

    private static IEnumerable<TileTabGroupRowViewModel> BuildTileRows(IReadOnlyList<TerminalTabGroupViewModel> groups)
    {
        var count = groups.Count;
        if (count == 0)
            yield break;

        if (count <= 2)
        {
            yield return new TileTabGroupRowViewModel(groups);
            yield break;
        }

        if (count == 3)
        {
            yield return new TileTabGroupRowViewModel(groups.Take(1));
            yield return new TileTabGroupRowViewModel(groups.Skip(1));
            yield break;
        }

        var rowCount = Math.Max(1, (int)Math.Floor(Math.Sqrt(count)));
        var baseColumns = count / rowCount;
        var remainder = count % rowCount;
        var index = 0;
        while (index < count)
        {
            var remainingRows = rowCount - (TileRowsBefore(index, baseColumns, remainder, rowCount));
            var take = baseColumns;
            if (remainder > 0 && remainingRows <= remainder)
                take++;

            take = Math.Min(take, count - index);
            yield return new TileTabGroupRowViewModel(groups.Skip(index).Take(take));
            index += take;
        }
    }

    private static int TileRowsBefore(int itemIndex, int baseColumns, int remainder, int rowCount)
    {
        var rowsBefore = 0;
        var consumed = 0;
        while (rowsBefore < rowCount && consumed < itemIndex)
        {
            var rowsLeft = rowCount - rowsBefore;
            var rowSize = baseColumns + (remainder > 0 && rowsLeft <= remainder ? 1 : 0);
            consumed += rowSize;
            rowsBefore++;
        }

        return rowsBefore;
    }

    private void AddTabToActiveGroup(TerminalTabViewModel tab)
    {
        tab.Terminal.SetCommandSuggestionsEnabled(_sessionTreeVm.Settings.EnableCommandSuggestions);

        if (!IsTabArrangementEnabled)
        {
            Tabs.Add(tab);
            return;
        }

        var group = SelectedTabGroup ?? TabGroups.FirstOrDefault();
        if (group == null)
        {
            Tabs.Add(tab);
            BuildTabGroupsFromTabs();
            return;
        }

        var insertIndex = group.Tabs
            .Select(existingTab => Tabs.IndexOf(existingTab))
            .Where(index => index >= 0)
            .DefaultIfEmpty(Tabs.Count - 1)
            .Max() + 1;

        if (insertIndex >= 0 && insertIndex <= Tabs.Count)
            Tabs.Insert(insertIndex, tab);
        else
            Tabs.Add(tab);

        group.AddTab(tab);
        SetSelectedTabGroup(group);
    }

    private void UpdateStatusBar()
    {
        if (SelectedTab?.Vnc is { } vnc)
        {
            ConnectionStatusText = vnc.IsConnected ? "VNC connected" : "VNC disconnected";
            ConnectionStatusColor = new SolidColorBrush(vnc.IsConnected ? Color.Parse("#52C41A") : Colors.Gray);
            ConnectedHostInfo = $"{SelectedTab.Session.Host}:{(SelectedTab.Session.Port > 0 ? SelectedTab.Session.Port : 5900)}";
            TerminalSizeText = vnc.RemoteWidth > 0 && vnc.RemoteHeight > 0
                ? $"{vnc.RemoteWidth}x{vnc.RemoteHeight}"
                : string.Empty;
            return;
        }

        if (SelectedTab?.Rdp is { } rdp)
        {
            ConnectionStatusText = rdp.IsConnected ? "RDP connected" : rdp.StatusText;
            ConnectionStatusColor = new SolidColorBrush(rdp.IsConnected ? Color.Parse("#52C41A") : Colors.Gray);
            ConnectedHostInfo = BuildRdpHostInfo(SelectedTab.Session);
            TerminalSizeText = string.Empty;
            return;
        }

        if (SelectedTab?.FileTransfer is { } fileTransfer)
        {
            ConnectionStatusText = fileTransfer.IsConnected
                ? $"{SelectedTab.Session.Protocol} connected"
                : $"{SelectedTab.Session.Protocol} disconnected";
            ConnectionStatusColor = new SolidColorBrush(fileTransfer.IsConnected ? Color.Parse("#52C41A") : Colors.Gray);
            ConnectedHostInfo = BuildFileTransferHostInfo(SelectedTab.Session);
            TerminalSizeText = string.Empty;
            return;
        }

        var terminal = SelectedTab?.Terminal;
        if (terminal != null && terminal.IsConnected)
        {
            ConnectionStatusText = "Connected";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
            ConnectedHostInfo = terminal.HostInfo;
        }
        else
        {
            ConnectionStatusText = "Disconnected";
            ConnectionStatusColor = new SolidColorBrush(Colors.Gray);
            ConnectedHostInfo = string.Empty;
        }
    }

    private void UpdateTerminalSize()
    {
        if (SelectedTab?.Vnc is { } vnc)
        {
            TerminalSizeText = vnc.RemoteWidth > 0 && vnc.RemoteHeight > 0
                ? $"{vnc.RemoteWidth}x{vnc.RemoteHeight}"
                : string.Empty;
            return;
        }

        if (SelectedTab?.FileTransfer != null)
        {
            TerminalSizeText = string.Empty;
            return;
        }

        var terminal = SelectedTab?.Terminal;
        if (terminal != null)
        {
            TerminalSizeText = $"{terminal.Columns}x{terminal.Rows}";
        }
        else
        {
            TerminalSizeText = "80x24";
        }
    }

    [RelayCommand]
    private async Task NewSession()
    {
        var dialog = new SessionEditDialog();
        var vm = new SessionEditViewModel(_sessionTreeVm.CreateSession());
        dialog.DataContext = vm;
        SessionInfo? savedSession = null;
        var sessionAdded = false;
        dialog.SessionSaved += session =>
        {
            savedSession = session;
            if (!sessionAdded)
            {
                _sessionTreeVm.AddSession(session);
                sessionAdded = true;
                return;
            }

            _sessionTreeVm.UpdateSession(session);
        };

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }

        if (dialog.ShouldConnect && savedSession != null)
        {
            CloseSessionManagerWindow();
            await ConnectSession(savedSession);
        }
    }

    [RelayCommand]
    private async Task Connect()
    {
        var session = _sessionTreeVm.SelectedSession;
        if (session == null)
            return;

        await ConnectSession(session);
    }

    public async Task EditSessionAsync(SessionInfo session)
    {
        var dialog = new SessionEditDialog();
        var vm = new SessionEditViewModel(session);
        dialog.DataContext = vm;
        SessionInfo? savedSession = null;
        dialog.SessionSaved += saved =>
        {
            savedSession = saved;
            _sessionTreeVm.UpdateSession(saved);
            RefreshOpenTabsForSession(saved);
        };

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }

        if (dialog.ShouldConnect && savedSession != null)
        {
            CloseSessionManagerWindow();
            await ConnectSession(savedSession);
        }
    }

    public async Task ShowConnectionDiagnosticsAsync(SessionInfo session)
    {
        if (session.Protocol != SessionProtocol.SSH)
            return;

        var dialog = new ConnectionDiagnosticsWindow
        {
            DataContext = new ConnectionDiagnosticsViewModel(session, GetSavedPassword(session))
        };
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
            await dialog.ShowDialog(lifetime.MainWindow);
    }

    public async Task ShowConnectionAuditAsync()
    {
        ShowSettingsCenter(SettingsSection.ConnectionAudit);
        await Task.CompletedTask;
    }

    private void ShowSettingsCenter(SettingsSection section)
    {
        var owner = GetMainWindow();
        if (owner == null)
            return;

        if (_settingsCenterWindow != null)
        {
            if (_settingsCenterWindow.DataContext is SettingsCenterViewModel existingViewModel)
                existingViewModel.Select(section);
            _settingsCenterWindow.Activate();
            return;
        }

        var viewModel = new SettingsCenterViewModel(
            _sessionTreeVm.Settings,
            ApplyApplicationSettings,
            ApplyLanguage,
            ApplyThemeMode,
            _connectionAuditService,
            BuildAppVersion(),
            CheckForUpdatesCommand);
        viewModel.Select(section);
        _settingsCenterWindow = new SettingsCenterWindow(viewModel);
        _settingsCenterWindow.Closed += (_, _) => _settingsCenterWindow = null;
        _settingsCenterWindow.Show(owner);
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || GetMainWindow()?.Clipboard is not { } clipboard)
            return;

        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(false);
        }
        catch
        {
            // Clipboard access is best-effort and can fail while the app is closing.
        }
    }

    private void RefreshOpenTabsForSession(SessionInfo session)
    {
        foreach (var tab in Tabs.Where(tab => tab.Session.Id == session.Id))
        {
            tab.Session.Name = session.Name;
            SessionTreeViewModel.CopySessionValues(tab.Session, session);
            tab.Title = tab.Session.Name;
            tab.NotifyThemeChanged();
            tab.Terminal.RefreshSessionOptions();
            tab.Vnc?.RefreshSessionOptions(tab.Session);
            UpdateMonitor(tab);
        }
    }

    public void DeleteSession(SessionInfo session)
    {
        _sessionTreeVm.DeleteSession(session);
        RefreshRecentSessions();
        AddCurrentSessionToQuickBarCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddCurrentSessionToQuickBar()
    {
        var session = SelectedTab?.Session;
        return session != null && !_sessionTreeVm.IsQuickSession(session);
    }

    [RelayCommand(CanExecute = nameof(CanAddCurrentSessionToQuickBar))]
    private void AddCurrentSessionToQuickBar()
    {
        var session = SelectedTab?.Session;
        if (session == null)
            return;

        _sessionTreeVm.AddQuickSession(session);
        AddCurrentSessionToQuickBarCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ConnectQuickSession(SessionInfo? session)
    {
        if (session == null)
            return;

        await ConnectSession(session);
    }

    [RelayCommand]
    private async Task ConnectRecentSession(RecentSessionItemViewModel? item)
    {
        if (item == null)
            return;

        await ConnectSession(item.Session);
    }

    public void RefreshRecentSessions()
    {
        var selectedSessionId = SelectedRecentSession?.Session.Id;
        var sessionsById = _sessionTreeVm.GetAllSessions().ToDictionary(session => session.Id);
        var items = _connectionAuditService
            .ReadRecentSuccessfulConnections(ConnectionAuditService.MaximumEntries)
            .Where(entry => sessionsById.ContainsKey(entry.SessionId))
            .Take(6)
            .Select(entry => new RecentSessionItemViewModel(
                sessionsById[entry.SessionId],
                entry.LocalTimestamp))
            .ToArray();

        RecentSessions.Clear();
        foreach (var item in items)
            RecentSessions.Add(item);

        SelectedRecentSession = selectedSessionId.HasValue
            ? RecentSessions.FirstOrDefault(item => item.Session.Id == selectedSessionId.Value)
            : null;
        OnPropertyChanged(nameof(HasRecentSessions));
    }

    [RelayCommand]
    private async Task EditQuickSession(SessionInfo? session)
    {
        if (session == null)
            return;

        await EditSessionAsync(session);
        AddCurrentSessionToQuickBarCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveQuickSession(SessionInfo? session)
    {
        if (session == null)
            return;

        _sessionTreeVm.RemoveQuickSession(session);
        AddCurrentSessionToQuickBarCommand.NotifyCanExecuteChanged();
    }

    public void MoveQuickSession(SessionInfo? source, SessionInfo? target, bool insertAfter)
    {
        if (source == null || target == null)
            return;

        _sessionTreeVm.MoveQuickSession(source, target, insertAfter);
    }

    public void MoveTabWithinSameStrip(TerminalTabViewModel? source, TerminalTabViewModel? target, bool insertAfter)
    {
        if (source == null || target == null || source == target)
            return;

        var sourceGroup = IsTabArrangementEnabled ? FindTabGroup(source) : null;
        var targetGroup = IsTabArrangementEnabled ? FindTabGroup(target) : null;
        if (IsTabArrangementEnabled && (sourceGroup == null || sourceGroup != targetGroup))
            return;

        MoveItemBeforeOrAfter(Tabs, source, target, insertAfter);
        if (sourceGroup != null)
        {
            MoveItemBeforeOrAfter(sourceGroup.Tabs, source, target, insertAfter);
            sourceGroup.SelectedTab = source;
        }

        SelectedTab = source;
        ActivateTabGroupForSelectedTab(source);
    }

    private static void MoveItemBeforeOrAfter<T>(
        ObservableCollection<T> collection,
        T source,
        T target,
        bool insertAfter)
    {
        var oldIndex = collection.IndexOf(source);
        var targetIndex = collection.IndexOf(target);
        if (oldIndex < 0 || targetIndex < 0)
            return;

        var newIndex = targetIndex + (insertAfter ? 1 : 0);
        if (oldIndex < newIndex)
            newIndex--;

        if (oldIndex == newIndex)
            return;

        collection.Move(oldIndex, newIndex);
    }

    public IReadOnlyList<QuickCommandItem> GetQuickCommands(TerminalTabViewModel? tab)
    {
        if (tab?.IsTerminalSession != true || !tab.Terminal.IsConnected)
            return [];

        return QuickCommandService.GetCommands(tab.Session, tab.Terminal.SupportsPosixShellFeatures);
    }

    public void ExecuteQuickCommand(TerminalTabViewModel? tab, QuickCommandItem? command)
    {
        if (tab?.IsTerminalSession != true ||
            !tab.Terminal.IsConnected ||
            command == null ||
            string.IsNullOrWhiteSpace(command.CommandText))
        {
            return;
        }

        SendTerminalInput(tab, command.CommandText.TrimEnd() + "\r");
    }

    public bool ExecuteQuickCommandByIndex(int index)
    {
        var tab = SelectedTab;
        if (tab?.Session.AdvancedDisableQuickCommandShortcuts == true)
            return false;

        var commands = GetQuickCommands(tab);
        if (index < 0 || index >= commands.Count)
            return false;

        ExecuteQuickCommand(tab, commands[index]);
        return true;
    }

    private void CloseSessionManagerWindow()
    {
        var window = _sessionManagerWindow;
        if (window == null)
            return;

        _sessionManagerWindow = null;
        window.Close();
    }

    public Task ConnectSession(SessionInfo session)
    {
        return ConnectSession(session, null, null);
    }

    public async Task ConnectSession(SessionInfo session, string? passwordOverride, string? initialRemoteDirectory)
    {
        if (session.Protocol is SessionProtocol.SFTP or SessionProtocol.FTP)
        {
            await ConnectFileTransferSession(session, passwordOverride);
            return;
        }

        if (session.Protocol == SessionProtocol.RDP)
        {
            await ConnectRdpSession(session, passwordOverride);
            return;
        }

        if (session.Protocol == SessionProtocol.VNC)
        {
            await ConnectVncSession(session, passwordOverride);
            return;
        }

        if (session.Protocol is not (SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN or SessionProtocol.SERIAL))
        {
            ConnectionStatusText = $"Protocol {session.Protocol} does not support terminal connection yet";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            return;
        }

        string? password = string.IsNullOrEmpty(passwordOverride)
            ? GetSavedPassword(session)
            : passwordOverride;

        if (session.Protocol is SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN &&
            string.IsNullOrEmpty(password) &&
            SshAgentAuthService.ShouldPromptForPassword(session))
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
                return;
        }

        if (session.Protocol == SessionProtocol.SSH &&
            !await EnsureSshPrivateKeyPassphrasesAsync(session))
        {
            return;
        }

        var tab = new TerminalTabViewModel(session);
        tab.CloseRequested += CloseTab;
        tab.PropertyChanged += OnTerminalTabPropertyChanged;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;

        tab.Terminal.PropertyChanged += OnActiveTerminalPropertyChanged;
        tab.Terminal.RemoteCurrentDirectoryChanged += path => OnTerminalRemoteCurrentDirectoryChanged(tab, path);
        RecordAudit(session, ConnectionAuditEventType.ConnectStarted);

        try
        {
            ConnectionStatusText = "Connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));

            await tab.Terminal.ConnectAsync(session, password);

            if (tab.IsDisposed || !Tabs.Contains(tab))
                return;

            tab.ConnectedPassword = password;
            await UpdateCompanionPanelsAfterTerminalConnectAsync(tab);
            StartInitialRemoteDirectoryChange(tab, initialRemoteDirectory);
        }
        catch (Exception ex)
        {
            RecordAudit(session, ConnectionAuditEventType.Failed, ex.Message);
            ConnectionStatusText = $"Connection failed: {ex.Message}";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
        }
    }

    private async Task ConnectRdpSession(SessionInfo session, string? passwordOverride)
    {
        var password = string.IsNullOrEmpty(passwordOverride)
            ? GetSavedPassword(session)
            : passwordOverride;
        if (string.IsNullOrEmpty(password))
            password = await ShowPasswordDialog(session);

        if (password == null)
            return;

        var rdp = new RdpViewModel(session, password);
        var tab = new TerminalTabViewModel(session, rdp);
        tab.CloseRequested += CloseTab;
        tab.PropertyChanged += OnTerminalTabPropertyChanged;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;
        RecordAudit(session, ConnectionAuditEventType.ConnectStarted);

        IsTerminalFullScreen = false;
        ConnectionStatusText = "RDP ready";
        ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
        ConnectedHostInfo = BuildRdpHostInfo(session);
    }

    private static string BuildRdpHostInfo(SessionInfo session)
    {
        var host = string.IsNullOrWhiteSpace(session.Host) ? "RDP" : session.Host.Trim();
        var port = session.Port > 0 ? session.Port : 3389;
        return string.IsNullOrWhiteSpace(session.Username)
            ? $"{host}:{port}"
            : $"{session.Username}@{host}:{port}";
    }

    private static string BuildFileTransferHostInfo(SessionInfo session)
    {
        var host = string.IsNullOrWhiteSpace(session.Host) ? session.Protocol.ToString() : session.Host.Trim();
        var port = session.Port > 0 ? session.Port : (session.Protocol == SessionProtocol.FTP ? 21 : 22);
        return string.IsNullOrWhiteSpace(session.Username)
            ? $"{host}:{port}"
            : $"{session.Username}@{host}:{port}";
    }

    private static string BuildVncHostInfo(SessionInfo session)
    {
        var host = string.IsNullOrWhiteSpace(session.Host) ? "VNC" : session.Host.Trim();
        var port = session.Port > 0 ? session.Port : 5900;
        if (!session.VncUseSshTunnel)
            return $"{host}:{port}";

        var sshHost = string.IsNullOrWhiteSpace(session.VncSshHost) ? host : session.VncSshHost.Trim();
        var sshPort = session.VncSshPort is >= 1 and <= 65535 ? session.VncSshPort : 22;
        var remoteHost = string.IsNullOrWhiteSpace(session.VncSshRemoteHost) ? host : session.VncSshRemoteHost.Trim();
        var remotePort = session.VncSshRemotePort is >= 1 and <= 65535 ? session.VncSshRemotePort : port;
        return $"{remoteHost}:{remotePort} via SSH {sshHost}:{sshPort}";
    }

    private async Task ConnectVncSession(SessionInfo session, string? passwordOverride)
    {
        var password = string.IsNullOrEmpty(passwordOverride)
            ? GetSavedPassword(session)
            : passwordOverride;
        if (string.IsNullOrEmpty(password))
            password = await ShowPasswordDialog(session);
        if (password == null)
            return;

        var vm = new VncViewModel();
        var tab = new TerminalTabViewModel(session, vm);
        tab.CloseRequested += CloseTab;
        tab.PropertyChanged += OnTerminalTabPropertyChanged;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;
        IsSftpVisible = false;
        IsMonitorVisible = false;
        RecordAudit(session, ConnectionAuditEventType.ConnectStarted);

        try
        {
            ConnectionStatusText = "VNC connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            ConnectedHostInfo = BuildVncHostInfo(session);
            await vm.ConnectAsync(session, password);
            if (tab.IsDisposed || !Tabs.Contains(tab))
                return;

            ConnectionStatusText = "VNC connected";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
            UpdateTerminalSize();
        }
        catch (Exception ex)
        {
            RecordAudit(session, ConnectionAuditEventType.Failed, ex.Message);
            ConnectionStatusText = $"VNC failed: {ex.Message}";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
            vm.StatusText = $"VNC failed: {ex.Message}";
        }
    }

    private async Task ConnectFileTransferSession(SessionInfo session, string? passwordOverride)
    {
        string? password = string.IsNullOrEmpty(passwordOverride)
            ? GetSavedPassword(session)
            : passwordOverride;

        if (string.IsNullOrEmpty(password) && SshAgentAuthService.ShouldPromptForPassword(session))
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
                return;
        }

        if (session.Protocol == SessionProtocol.SFTP &&
            !await EnsureSshPrivateKeyPassphrasesAsync(session))
        {
            return;
        }

        IsTerminalFullScreen = false;
        IsSftpVisible = false;
        IsMonitorVisible = false;
        ConnectionStatusText = $"{session.Protocol} connecting...";
        ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
        ConnectedHostInfo = $"{session.Username}@{session.Host}:{session.Port}";

        var fileTransfer = new SftpViewModel();
        var tab = new TerminalTabViewModel(session, fileTransfer);
        tab.CloseRequested += CloseTab;
        tab.PropertyChanged += OnTerminalTabPropertyChanged;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;
        RecordAudit(session, ConnectionAuditEventType.ConnectStarted);

        var connected = await fileTransfer.SwitchConnectionAsync(session, password);
        if (tab.IsDisposed || !Tabs.Contains(tab))
            return;

        tab.ConnectedPassword = password;
        if (connected)
        {
            ConnectionStatusText = $"{session.Protocol} connected";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
        }
        else
        {
            RecordAudit(session, ConnectionAuditEventType.Failed, $"{session.Protocol} connection failed");
            ConnectionStatusText = $"{session.Protocol} connection failed";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
        }
    }

    private void OnActiveTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.IsConnected))
            NotifyKeyboardBroadcastStateChanged();

        // Only respond if this is the currently selected tab's terminal
        if (sender != SelectedTab?.Terminal) return;

        if (e.PropertyName == nameof(TerminalViewModel.IsConnected) ||
            e.PropertyName == nameof(TerminalViewModel.HostInfo))
        {
            UpdateStatusBar();
            CurrentConnectCommand.NotifyCanExecuteChanged();
            CurrentDisconnectCommand.NotifyCanExecuteChanged();
        }
        if (e.PropertyName == nameof(TerminalViewModel.Columns) ||
            e.PropertyName == nameof(TerminalViewModel.Rows))
        {
            UpdateTerminalSize();
        }
    }

    private async void OnTerminalRemoteCurrentDirectoryChanged(TerminalTabViewModel tab, string path)
    {
        if (tab != SelectedTab ||
            !IsSftpVisible ||
            !tab.CompanionSftp.IsConnected ||
            tab.Session.Protocol != SessionProtocol.SSH ||
            !tab.Session.SftpFollowTerminalDirectory ||
            tab.Vnc != null ||
            tab.Rdp != null ||
            tab.FileTransfer != null)
        {
            return;
        }

        await tab.CompanionSftp.TryNavigateToRemotePathAsync(path);
    }

    public async Task DuplicateTab(TerminalTabViewModel? source)
    {
        if (source == null)
            return;

        var initialRemoteDirectory = GetDuplicateInitialRemoteDirectory(source);
        await ConnectSession(source.Session, source.ConnectedPassword, initialRemoteDirectory);
    }

    private static string? GetDuplicateInitialRemoteDirectory(TerminalTabViewModel source)
    {
        if (!source.IsTerminalSession ||
            source.Session.Protocol != SessionProtocol.SSH ||
            !source.Session.TerminalAdvancedDuplicateSessionCd ||
            string.IsNullOrWhiteSpace(source.Terminal.RemoteCurrentDirectory))
        {
            return null;
        }

        return source.Terminal.RemoteCurrentDirectory;
    }

    private async void StartInitialRemoteDirectoryChange(TerminalTabViewModel tab, string? remoteDirectory)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory) ||
            tab.Session.Protocol != SessionProtocol.SSH)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            if (!Tabs.Contains(tab) || !tab.Terminal.IsConnected)
                return;

            var command = BuildRemoteChangeDirectoryCommand(remoteDirectory, tab.Terminal.SupportsPosixShellFeatures);
            if (!string.IsNullOrWhiteSpace(command))
                tab.Terminal.SendInput(command);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Duplicate tab cd failed: {ex.Message}");
        }
    }

    private void OnTerminalTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TerminalTabViewModel tab && e.PropertyName == nameof(TerminalTabViewModel.IsConnected))
        {
            RecordAudit(
                tab.Session,
                tab.IsConnected ? ConnectionAuditEventType.Connected : ConnectionAuditEventType.Disconnected);
            AgentPanel.RefreshSessions();
        }

        if (e.PropertyName == nameof(TerminalTabViewModel.IsKeyboardBroadcastEnabled))
            NotifyKeyboardBroadcastStateChanged();
    }

    private void RecordAudit(
        SessionInfo session,
        ConnectionAuditEventType eventType,
        string? detail = null)
    {
        _connectionAuditService.Record(session, eventType, detail);
    }

    private static string BuildRemoteChangeDirectoryCommand(string remoteDirectory, bool supportsPosixShellFeatures)
    {
        return supportsPosixShellFeatures
            ? $"cd {QuotePosixShellArgument(remoteDirectory)}\r"
            : $"cd /d \"{EscapeWindowsCommandArgument(ToWindowsRemotePath(remoteDirectory))}\"\r";
    }

    private static string QuotePosixShellArgument(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string ToWindowsRemotePath(string remoteDirectory)
    {
        var value = remoteDirectory.Trim();
        if (value.Length >= 4 &&
            value[0] == '/' &&
            char.IsLetter(value[1]) &&
            value[2] == ':' &&
            value[3] == '/')
        {
            return $"{char.ToUpperInvariant(value[1])}:{value[3..].Replace('/', '\\')}";
        }

        return value.Replace('/', '\\');
    }

    private static string EscapeWindowsCommandArgument(string value)
    {
        return value.Replace("\"", "\"\"", StringComparison.Ordinal);
    }

    public void CloseTab(TerminalTabViewModel tab)
    {
        if (tab == null || !Tabs.Contains(tab))
            return;

        var wasSelected = SelectedTab == tab;
        var group = IsTabArrangementEnabled ? FindTabGroup(tab) : null;
        RecordAudit(tab.Session, ConnectionAuditEventType.TabClosed);

        tab.PropertyChanged -= OnTerminalTabPropertyChanged;
        tab.Terminal.PropertyChanged -= OnActiveTerminalPropertyChanged;

        group?.RemoveTab(tab);
        if (group is { HasTabs: false })
            TabGroups.Remove(group);

        Tabs.Remove(tab);
        if (ReferenceEquals(Sftp, tab.CompanionSftp) ||
            ReferenceEquals(Sftp, tab.FileTransfer))
        {
            Sftp = _emptySftp;
        }
        tab.Dispose();

        if (wasSelected && Tabs.Count > 0)
        {
            SelectedTab = group?.SelectedTab ?? SelectedTabGroup?.SelectedTab ?? Tabs.Last();
        }

        if (Tabs.Count == 0)
        {
            IsTerminalFullScreen = false;
            TabGroups.Clear();
            SetSelectedTabGroup(null);
            StopAllMonitors();
            IsSftpVisible = false;
            IsMonitorVisible = false;
            Sftp = _emptySftp;
            UpdateStatusBar();
            UpdateTerminalSize();
            return;
        }

        if (IsTabArrangementEnabled)
        {
            if (TabGroups.Count < 2)
            {
                MergeTabGroups();
            }
            else
            {
                ActivateTabGroupForSelectedTab(SelectedTab);
            }
        }
    }

    /// <summary>
    /// Closes all tabs and releases the window-level companion view models.
    /// This is also used by the desktop window shutdown path so connections do
    /// not outlive the main window.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        foreach (var tab in Tabs.ToList())
            CloseTab(tab);

        _emptySftp.Dispose();
        _emptyMonitor.Dispose();
        AgentPanel.PropertyChanged -= OnAgentPanelPropertyChanged;
        AgentPanel.Dispose();
        AgentRuntimeSession.Dispose();
        (AgentRuntimeHost as IDisposable)?.Dispose();
        (AgentRunCoordinator as IDisposable)?.Dispose();
        (AgentSessionGateway as IDisposable)?.Dispose();
    }

    private IReadOnlyList<IAgentSessionEndpoint> BuildAgentSessionEndpoints()
    {
        return Tabs
            .Where(tab => tab.IsTerminalSession)
            .GroupBy(tab => tab.Session.Id)
            .Select(group => CreateAgentSessionEndpoint(group.First()))
            .ToList();
    }

    private IAgentSessionEndpoint CreateAgentSessionEndpoint(TerminalTabViewModel tab)
    {
        return new AgentSessionEndpoint(
            () => AgentSessionSnapshot.FromSession(
                tab.Session,
                tab.IsConnected && tab.Terminal.IsConnected && !tab.IsDisposed,
                tab.Terminal.SupportsPosixShellFeatures ? "Linux/Unix" : "Windows"),
            async (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (tab.IsDisposed)
                    throw new AgentCommandDeliveryException(
                        AgentCommandStatus.SessionNotFound,
                        "The requested session tab has been closed.");

                if (!tab.IsTerminalSession || tab.Session.Protocol != SessionProtocol.SSH)
                    throw new AgentCommandDeliveryException(
                        AgentCommandStatus.UnsupportedProtocol,
                        "Only SSH terminal sessions are supported.");

                if (!tab.IsConnected || !tab.Terminal.IsConnected)
                    throw new AgentCommandDeliveryException(
                        AgentCommandStatus.SessionNotConnected,
                        "The requested session is no longer connected.");

                var payload = request.Command;
                if (request.AppendLineEnding &&
                    !payload.EndsWith('\r') &&
                    !payload.EndsWith('\n'))
                {
                    payload += "\n";
                }

                payload = TerminalSessionOptions.NormalizeSendLineEndings(payload, tab.Session);
                cancellationToken.ThrowIfCancellationRequested();
                tab.Terminal.SendInput(payload);
                await Task.CompletedTask;
            },
            runCommand: null,
            runCommandResult: async (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tab.IsDisposed || !tab.IsConnected || !tab.Terminal.IsConnected)
                    throw new AgentCommandDeliveryException(
                        AgentCommandStatus.SessionNotConnected,
                        "The requested session is no longer connected.");

                return await tab.Terminal.RunAgentCommandAsync(
                    request.RequestId,
                    request.Command,
                    request.Timeout,
                    cancellationToken,
                    request.DisplayCommand,
                    request.SensitiveInput).ConfigureAwait(false);
            },
            runCommandProgressResult: async (request, cancellationToken, progressReceived) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tab.IsDisposed || !tab.IsConnected || !tab.Terminal.IsConnected)
                    throw new AgentCommandDeliveryException(
                        AgentCommandStatus.SessionNotConnected,
                        "The requested session is no longer connected.");

                return await tab.Terminal.RunAgentCommandAsync(
                    request.RequestId,
                    request.Command,
                    request.Timeout,
                    cancellationToken,
                    request.DisplayCommand,
                    request.SensitiveInput,
                    progressReceived).ConfigureAwait(false);
            });
    }

    [RelayCommand]
    private void SelectTab(TerminalTabViewModel tab)
    {
        SelectedTab = tab;
    }

    [RelayCommand]
    private void SelectTabGroup(TerminalTabGroupViewModel? group)
    {
        if (group?.SelectedTab == null)
            return;

        SetSelectedTabGroup(group);
        SelectedTab = group.SelectedTab;
    }

    [RelayCommand]
    private void Disconnect()
    {
        if (SelectedTab?.Vnc != null)
            SelectedTab.Vnc.Disconnect();
        else if (SelectedTab?.Rdp != null)
            SelectedTab.Rdp.Disconnect();
        else if (SelectedTab?.FileTransfer != null)
            SelectedTab.FileTransfer.StopBrowsing();
        else
        {
            SelectedTab?.Terminal.Disconnect();
            SelectedTab?.CompanionSftp.StopBrowsing();
            SelectedTab?.Monitor.StopMonitoring();
        }
    }

    private bool CanCurrentConnect()
    {
        return SelectedTab != null &&
               (SelectedTab.Vnc != null
                   ? !SelectedTab.Vnc.IsConnected
                   : SelectedTab.Rdp != null
                       ? !SelectedTab.Rdp.IsConnected
                   : SelectedTab.FileTransfer != null
                       ? !SelectedTab.FileTransfer.IsConnected
                   : !SelectedTab.Terminal.IsConnected);
    }

    [RelayCommand(CanExecute = nameof(CanCurrentConnect))]
    private async Task CurrentConnect()
    {
        var tab = SelectedTab;
        if (tab == null)
            return;

        RecordAudit(tab.Session, ConnectionAuditEventType.ConnectStarted);

        if (tab.Vnc != null)
        {
            if (tab.Vnc.IsConnected)
                return;

            var vncPassword = tab.ConnectedPassword ?? GetSavedPassword(tab.Session) ?? await ShowPasswordDialog(tab.Session);
            if (vncPassword == null)
                return;

            try
            {
                ConnectionStatusText = "VNC connecting...";
                ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
                await tab.Vnc.ConnectAsync(tab.Session, vncPassword);
                tab.ConnectedPassword = vncPassword;
                UpdateStatusBar();
                UpdateTerminalSize();
            }
            catch (Exception ex)
            {
                RecordAudit(tab.Session, ConnectionAuditEventType.Failed, ex.Message);
                ConnectionStatusText = $"VNC failed: {ex.Message}";
                ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
            }

            return;
        }

        if (tab.Rdp != null)
        {
            if (tab.Rdp.IsConnected)
                return;

            ConnectionStatusText = "RDP connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            tab.Rdp.Reconnect();
            UpdateStatusBar();
            return;
        }

        if (tab.FileTransfer != null)
        {
            if (tab.FileTransfer.IsConnected)
                return;

            var filePassword = tab.ConnectedPassword ?? GetSavedPassword(tab.Session) ?? await ShowPasswordDialog(tab.Session);
            if (filePassword == null)
                return;

            if (tab.Session.Protocol == SessionProtocol.SFTP &&
                !await EnsureSshPrivateKeyPassphrasesAsync(tab.Session))
            {
                return;
            }

            ConnectionStatusText = $"{tab.Session.Protocol} connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            await tab.FileTransfer.SwitchConnectionAsync(tab.Session, filePassword);
            tab.ConnectedPassword = filePassword;
            if (!tab.FileTransfer.IsConnected)
                RecordAudit(tab.Session, ConnectionAuditEventType.Failed, $"{tab.Session.Protocol} connection failed");
            UpdateStatusBar();
            return;
        }

        if (tab.Terminal.IsConnected)
            return;

        var password = tab.ConnectedPassword ?? GetSavedPassword(tab.Session);
        if (tab.Session.Protocol is SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN &&
            SshAgentAuthService.ShouldPromptForPassword(tab.Session) &&
            password == null)
        {
            password = await ShowPasswordDialog(tab.Session);
            if (password == null)
                return;
        }

        if (tab.Session.Protocol == SessionProtocol.SSH &&
            !await EnsureSshPrivateKeyPassphrasesAsync(tab.Session))
        {
            return;
        }

        try
        {
            ConnectionStatusText = "Connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            await tab.Terminal.ConnectAsync(tab.Session, password);
            tab.ConnectedPassword = password;
            await UpdateCompanionPanelsAfterTerminalConnectAsync(tab);
        }
        catch (Exception ex)
        {
            RecordAudit(tab.Session, ConnectionAuditEventType.Failed, ex.Message);
            ConnectionStatusText = $"Connection failed: {ex.Message}";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
        }
    }

    private bool CanCurrentDisconnect()
    {
        return SelectedTab?.Vnc?.IsConnected == true ||
               SelectedTab?.Rdp?.IsConnected == true ||
               SelectedTab?.FileTransfer?.IsConnected == true ||
               SelectedTab?.Terminal.IsConnected == true;
    }

    [RelayCommand(CanExecute = nameof(CanCurrentDisconnect))]
    private void CurrentDisconnect()
    {
        if (SelectedTab?.Vnc != null)
            SelectedTab.Vnc.Disconnect();
        else if (SelectedTab?.Rdp != null)
            SelectedTab.Rdp.Disconnect();
        else if (SelectedTab?.FileTransfer != null)
            SelectedTab.FileTransfer.StopBrowsing();
        else
        {
            SelectedTab?.Terminal.Disconnect("[Current session disconnected]");
            SelectedTab?.CompanionSftp.StopBrowsing();
            SelectedTab?.Monitor.StopMonitoring();
        }

        UpdateStatusBar();
        UpdateTerminalSize();
    }

    private static string? GetSavedPassword(SessionInfo session)
    {
        var password = PasswordEncryptionService.Decrypt(session.Password);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    private async Task<bool> EnsureSshPrivateKeyPassphrasesAsync(SessionInfo session)
    {
        if (session.Protocol is not (SessionProtocol.SSH or SessionProtocol.SFTP))
            return true;

        if (!await EnsureSessionPrivateKeyPassphraseAsync(session))
            return false;

        foreach (var proxy in EnumerateJumpHostChain(session))
        {
            if (!await EnsureProxyPrivateKeyPassphraseAsync(session, proxy))
                return false;
        }

        return true;
    }

    private async Task<bool> EnsureSessionPrivateKeyPassphraseAsync(SessionInfo session)
    {
        if (session.AuthMethod != AuthMethod.PrivateKey ||
            string.IsNullOrWhiteSpace(session.PrivateKeyPath) ||
            SshAgentAuthService.HasPrivateKeyPassphrase(session) ||
            !SshAgentAuthService.RequiresPrivateKeyPassphrase(session.PrivateKeyPath))
        {
            return true;
        }

        var detail = string.Format(
            _localization.Text("PrivateKeyPassphraseDialog.Key"),
            session.PrivateKeyPath);
        var prompt = await ShowPrivateKeyPassphraseDialog(
            string.Format(_localization.Text("PrivateKeyPassphraseDialog.Title"), session.Name),
            detail,
            PasswordEncryptionService.HasSavedPassword(session.PrivateKeyPassphrase));
        if (prompt == null)
            return false;

        session.RuntimePrivateKeyPassphrase = prompt.Passphrase;
        if (prompt.Save && !string.IsNullOrEmpty(prompt.Passphrase))
            SaveSessionPrivateKeyPassphrase(session, prompt.Passphrase);

        return true;
    }

    private async Task<bool> EnsureProxyPrivateKeyPassphraseAsync(SessionInfo session, ProxySettings proxy)
    {
        if (proxy.AuthMethod != AuthMethod.PrivateKey ||
            string.IsNullOrWhiteSpace(proxy.PrivateKeyPath) ||
            SshAgentAuthService.HasPrivateKeyPassphrase(proxy) ||
            !SshAgentAuthService.RequiresPrivateKeyPassphrase(proxy.PrivateKeyPath))
        {
            return true;
        }

        var displayName = string.IsNullOrWhiteSpace(proxy.DisplayName)
            ? $"{proxy.Username}@{proxy.Host}:{proxy.Port}"
            : proxy.DisplayName;
        var detail = string.Format(
            _localization.Text("PrivateKeyPassphraseDialog.JumpHost"),
            displayName,
            proxy.PrivateKeyPath);
        var prompt = await ShowPrivateKeyPassphraseDialog(
            string.Format(_localization.Text("PrivateKeyPassphraseDialog.Title"), displayName),
            detail,
            PasswordEncryptionService.HasSavedPassword(proxy.PrivateKeyPassphrase));
        if (prompt == null)
            return false;

        proxy.RuntimePrivateKeyPassphrase = prompt.Passphrase;
        if (prompt.Save && !string.IsNullOrEmpty(prompt.Passphrase))
            SaveProxyPrivateKeyPassphrase(session, proxy, prompt.Passphrase);

        return true;
    }

    private void SaveSessionPrivateKeyPassphrase(SessionInfo session, string passphrase)
    {
        var encrypted = PasswordEncryptionService.Encrypt(passphrase);
        session.PrivateKeyPassphrase = encrypted;
        _sessionTreeVm.UpdateSessionSecret(session.Id, saved => saved.PrivateKeyPassphrase = encrypted);

        foreach (var tab in Tabs.Where(tab => tab.Session.Id == session.Id))
            tab.Session.PrivateKeyPassphrase = encrypted;
    }

    private void SaveProxyPrivateKeyPassphrase(SessionInfo session, ProxySettings proxy, string passphrase)
    {
        var encrypted = PasswordEncryptionService.Encrypt(passphrase);
        proxy.PrivateKeyPassphrase = encrypted;
        _sessionTreeVm.UpdateSessionSecret(session.Id, saved =>
        {
            UpdateProxyPrivateKeyPassphrase(saved.Proxy, proxy.Id, encrypted);
            foreach (var savedProxy in saved.ProxyServers)
                UpdateProxyPrivateKeyPassphrase(savedProxy, proxy.Id, encrypted);
        });

        foreach (var tab in Tabs.Where(tab => tab.Session.Id == session.Id))
        {
            UpdateProxyPrivateKeyPassphrase(tab.Session.Proxy, proxy.Id, encrypted);
            foreach (var tabProxy in tab.Session.ProxyServers)
                UpdateProxyPrivateKeyPassphrase(tabProxy, proxy.Id, encrypted);
        }
    }

    private static void UpdateProxyPrivateKeyPassphrase(ProxySettings? proxy, Guid proxyId, string encryptedPassphrase)
    {
        if (proxy?.Id == proxyId)
            proxy.PrivateKeyPassphrase = encryptedPassphrase;
    }

    private static IEnumerable<ProxySettings> EnumerateJumpHostChain(SessionInfo session)
    {
        var proxy = session.Proxy;
        if (proxy == null || !proxy.IsEnabled || proxy.Protocol != ProxyProtocol.JumpHost)
            yield break;

        var proxiesById = session.ProxyServers
            .Where(item => item.IsEnabled)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var visited = new HashSet<Guid>();
        while (proxy is { IsEnabled: true, Protocol: ProxyProtocol.JumpHost })
        {
            if (!visited.Add(proxy.Id))
                yield break;

            yield return proxy;
            if (!proxy.NextProxyId.HasValue ||
                !proxiesById.TryGetValue(proxy.NextProxyId.Value, out var nextProxy))
            {
                yield break;
            }

            proxy = nextProxy;
        }
    }

    private static AtomUI.Desktop.Controls.Window? GetMainWindow()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return lifetime?.MainWindow as AtomUI.Desktop.Controls.Window;
    }

    private static Avalonia.Controls.Window? GetActiveWindow()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return lifetime?.Windows.FirstOrDefault(window => window.IsActive) ?? lifetime?.MainWindow;
    }

    private sealed record PrivateKeyPassphrasePromptResult(string Passphrase, bool Save);

    private async Task<PrivateKeyPassphrasePromptResult?> ShowPrivateKeyPassphraseDialog(
        string title,
        string detail,
        bool hasSavedPassphrase)
    {
        var dialog = new AtomUI.Desktop.Controls.Window
        {
            Title = title,
            Width = 480,
            Height = 238,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = Brushes.White
        };

        var passphraseBox = new AtomUI.Desktop.Controls.LineEdit
        {
            PasswordChar = '*',
            PlaceholderText = _localization.Text("PrivateKeyPassphraseDialog.Placeholder"),
            IsEnableRevealButton = true,
            IsAllowClear = true,
            SizeType = CustomizableSizeType.Middle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 34,
            Margin = new Thickness(20, 10, 20, 0)
        };

        var savePassphraseBox = new AtomUI.Desktop.Controls.CheckBox
        {
            Content = _localization.Text("PrivateKeyPassphraseDialog.SavePassphrase"),
            IsChecked = hasSavedPassphrase,
            Margin = new Thickness(20, 0, 20, 0)
        };

        PrivateKeyPassphrasePromptResult? result = null;

        void Confirm()
        {
            result = new PrivateKeyPassphrasePromptResult(
                passphraseBox.Text ?? string.Empty,
                savePassphraseBox.IsChecked == true);
            dialog.Close();
        }

        var okButton = new AtomUI.Desktop.Controls.Button
        {
            Content = _localization.Text("PasswordDialog.Ok"),
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary,
            SizeType = CustomizableSizeType.Middle
        };
        okButton.Click += (_, _) => Confirm();

        var cancelButton = new AtomUI.Desktop.Controls.Button
        {
            Content = _localization.Text("PasswordDialog.Cancel"),
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Default,
            SizeType = CustomizableSizeType.Middle
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        var contentPanel = new StackPanel { Spacing = 8 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 20, 20, 0)
        });
        contentPanel.Children.Add(passphraseBox);
        contentPanel.Children.Add(savePassphraseBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 4, 20, 16)
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(contentPanel);
        root.Children.Add(buttonPanel);

        passphraseBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.Content = root;
        dialog.Opened += (_, _) => passphraseBox.Focus();

        var mainWindow = GetMainWindow();
        if (mainWindow != null)
            await dialog.ShowDialog(mainWindow);

        return result;
    }

    private async Task<string?> ShowPasswordDialog(SessionInfo session)
    {
        var dialog = new AtomUI.Desktop.Controls.Window
        {
            Title = string.Format(_localization.Text("PasswordDialog.Title"), session.Name),
            Width = 460,
            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = Brushes.White
        };

        var passwordBox = new AtomUI.Desktop.Controls.LineEdit
        {
            PasswordChar = '*',
            PlaceholderText = _localization.Text("PasswordDialog.Placeholder"),
            IsEnableRevealButton = true,
            IsAllowClear = true,
            SizeType = CustomizableSizeType.Middle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 34,
            Margin = new Thickness(20, 10, 20, 0)
        };

        var savePasswordBox = new AtomUI.Desktop.Controls.CheckBox
        {
            Content = _localization.Text("PasswordDialog.SavePassword"),
            IsChecked = PasswordEncryptionService.HasSavedPassword(session.Password),
            Margin = new Thickness(20, 0, 20, 0)
        };

        string? result = null;

        void Confirm()
        {
            result = passwordBox.Text;
            if (savePasswordBox.IsChecked == true && !string.IsNullOrEmpty(result))
            {
                session.Password = PasswordEncryptionService.Encrypt(result);
                _sessionTreeVm.UpdateSession(session);
                RefreshOpenTabsForSession(session);
            }

            dialog.Close();
        }

        var okButton = new AtomUI.Desktop.Controls.Button
        {
            Content = _localization.Text("PasswordDialog.Ok"),
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary,
            SizeType = CustomizableSizeType.Middle
        };
        okButton.Click += (_, _) =>
        {
            Confirm();
        };

        var cancelButton = new AtomUI.Desktop.Controls.Button
        {
            Content = _localization.Text("PasswordDialog.Cancel"),
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Default,
            SizeType = CustomizableSizeType.Middle
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        var contentPanel = new StackPanel { Spacing = 8 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = string.Format(_localization.Text("PasswordDialog.User"), session.Username, session.Host, session.Port),
            Margin = new Thickness(20, 20, 20, 0)
        });
        contentPanel.Children.Add(passwordBox);
        contentPanel.Children.Add(savePasswordBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 4, 20, 16)
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(contentPanel);
        root.Children.Add(buttonPanel);

        passwordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.Content = root;
        dialog.Opened += (_, _) => passwordBox.Focus();

        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            await dialog.ShowDialog(mainWindow);
        }

        return result;
    }
}
