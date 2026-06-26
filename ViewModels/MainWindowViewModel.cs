using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AtomUI;
using AtomUI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using ChiXueSsh.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public enum TabArrangementMode
{
    Single,
    Vertical,
    Horizontal,
    Tile
}

public partial class MainWindowViewModel : ObservableObject
{
    private const double DefaultSftpPanelWidth = 318;
    private const double MinimumSftpPanelWidth = 260;

    private readonly SessionTreeViewModel _sessionTreeVm;
    private readonly LocalizationService _localization = LocalizationService.Shared;

    [ObservableProperty] private SessionTreeViewModel _sessionTree;
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
    [ObservableProperty] private TabArrangementMode _tabArrangementMode = TabArrangementMode.Single;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();
    public ObservableCollection<TerminalTabGroupViewModel> TabGroups { get; } = new();
    public ObservableCollection<TileTabGroupRowViewModel> TileRows { get; } = new();

    [ObservableProperty] private TerminalTabViewModel? _selectedTab;
    [ObservableProperty] private TerminalTabGroupViewModel? _selectedTabGroup;

    public bool HasTabs => Tabs.Count > 0;
    public bool IsMainChromeVisible => !IsTerminalFullScreen;
    public bool IsSftpPanelVisible => IsSftpVisible && !IsTerminalFullScreen;
    public double SftpPanelPixelWidth => SftpPanelWidth.Value;
    public GridLength SftpSplitterWidth => IsSftpPanelVisible ? new GridLength(8) : new GridLength(0);
    public bool IsMonitorPanelVisible => IsMonitorVisible && !IsTerminalFullScreen;
    public bool IsTabHeaderVisible => !IsTerminalFullScreen;
    public bool IsMainTabHeaderVisible => IsTabHeaderVisible && !IsTabArrangementEnabled;
    public bool IsSingleTabContentVisible => HasTabs && (!IsTabArrangementEnabled || IsTerminalFullScreen);
    public bool IsArrangedTabsVisible => HasTabs && IsTabArrangementEnabled && !IsTerminalFullScreen;
    public bool IsTabArrangementEnabled => TabArrangementMode != TabArrangementMode.Single;
    public bool IsVerticalTabArrangement => TabArrangementMode == TabArrangementMode.Vertical;
    public bool IsHorizontalTabArrangement => TabArrangementMode == TabArrangementMode.Horizontal;
    public bool IsTileTabArrangement => TabArrangementMode == TabArrangementMode.Tile;
    public bool CanArrangeTabs => Tabs.Count >= 2;
    public bool CanMergeTabGroups => IsTabArrangementEnabled;
    public bool IsSelectedTerminalSession => SelectedTab?.IsTerminalSession == true;
    public bool IsSelectedVncSession => SelectedTab?.IsVncSession == true;
    public bool IsSelectedRdpSession => SelectedTab?.IsRdpSession == true;
    public bool IsSelectedFileTransferSession => SelectedTab?.IsFileTransferSession == true;

    public ServerMonitorViewModel Monitor { get; } = new();
    public SftpViewModel Sftp { get; } = new();
    public ObservableCollection<SessionInfo> QuickSessions => _sessionTreeVm.QuickSessions;
    public string ThemeIcon => IsDarkMode ? "\u263E" : "\u2600";
    public string LanguageIcon => _localization.IsEnglish ? "EN" : "中";
    public string NewSessionText => _localization.Text("Toolbar.New");
    public string NewSessionToolTip => _localization.Text("Toolbar.NewTip");
    public string SessionManagerText => _localization.Text("Toolbar.Sessions");
    public string SessionManagerToolTip => _localization.Text("Toolbar.SessionsTip");
    public string ConnectText => _localization.Text("Toolbar.Connect");
    public string ConnectToolTip => _localization.Text("Toolbar.ConnectTip");
    public string DisconnectText => _localization.Text("Toolbar.Disconnect");
    public string DisconnectToolTip => _localization.Text("Toolbar.DisconnectTip");
    public string SftpToolTip => _localization.Text("Toolbar.SftpTip");
    public string MonitorText => _localization.Text("Toolbar.Monitor");
    public string MonitorToolTip => _localization.Text("Toolbar.MonitorTip");
    public string ThemeToolTip => _localization.Text("Toolbar.ThemeTip");
    public string FullScreenToolTip => _localization.Text("Toolbar.FullScreenTip");
    public string ArrangeText => _localization.Text("Toolbar.Arrange");
    public string ArrangeToolTip => _localization.Text("Toolbar.ArrangeTip");
    public string LanguageToolTip => _localization.Text("Toolbar.LanguageTip");
    public string AddQuickSessionToolTip => _localization.Text("Toolbar.AddQuickSessionTip");
    public string ArrangeVerticalText => _localization.Text("Arrange.Vertical");
    public string ArrangeHorizontalText => _localization.Text("Arrange.Horizontal");
    public string ArrangeTileText => _localization.Text("Arrange.Tile");
    public string ArrangeMergeText => _localization.Text("Arrange.Merge");
    public string QuickPropertiesText => _localization.Text("Quick.Properties");
    public string QuickDeleteText => _localization.Text("Quick.Delete");
    public string TabCloseText => _localization.Text("TabMenu.Close");
    public string TabPropertiesText => _localization.Text("TabMenu.Properties");
    public string TabAddQuickText => _localization.Text("TabMenu.AddQuick");
    public string WelcomeSelectSessionText => _localization.Text("Welcome.SelectSession");
    public string WelcomeBuiltWithAtomUiText => _localization.Text("Welcome.BuiltWithAtomUI");
    public string FullScreenEscBackText => _localization.Text("FullScreen.EscBack");
    public string ChineseLanguageText => _localization.Text("Language.Chinese");
    public string EnglishLanguageText => _localization.Text("Language.English");

    private Window? _sessionManagerWindow;
    public MainWindowViewModel()
    {
        _sessionTreeVm = new SessionTreeViewModel(this);
        _sessionTree = _sessionTreeVm;
        _localization.SetLanguage(_sessionTreeVm.Settings.UiLanguage);

        Tabs.CollectionChanged += (_, _) =>
        {
            if (Tabs.Count < 2 && TabArrangementMode != TabArrangementMode.Single)
                MergeTabGroups();

            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(CanArrangeTabs));
            RebuildTileRows();
            OnPropertyChanged(nameof(IsSingleTabContentVisible));
            OnPropertyChanged(nameof(IsArrangedTabsVisible));
            OnPropertyChanged(nameof(IsVerticalTabArrangement));
            OnPropertyChanged(nameof(IsHorizontalTabArrangement));
            OnPropertyChanged(nameof(IsTileTabArrangement));
            ToggleTerminalFullScreenCommand.NotifyCanExecuteChanged();
            ArrangeTabsVerticalCommand.NotifyCanExecuteChanged();
            ArrangeTabsHorizontalCommand.NotifyCanExecuteChanged();
            ArrangeTabsTileCommand.NotifyCanExecuteChanged();
            MergeTabGroupsCommand.NotifyCanExecuteChanged();
        };

        TabGroups.CollectionChanged += (_, _) =>
        {
            RebuildTileRows();
        };

        // Initialize theme state
        _isDarkMode = Application.Current?.IsDarkThemeMode() ?? false;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        var newMode = !IsDarkMode;
        app.SetDarkThemeMode(newMode);
        IsDarkMode = newMode;

        foreach (var tab in Tabs)
        {
            tab.NotifyThemeChanged();
        }
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeIcon));
    }

    [RelayCommand]
    private void SetLanguage(string? language)
    {
        _localization.SetLanguage(language);
        _sessionTreeVm.Settings.UiLanguage = _localization.Language;
        _sessionTreeVm.SaveSettings(_sessionTreeVm.Settings);
        NotifyLocalizationChanged();
    }

    private void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(LanguageIcon));
        OnPropertyChanged(nameof(NewSessionText));
        OnPropertyChanged(nameof(NewSessionToolTip));
        OnPropertyChanged(nameof(SessionManagerText));
        OnPropertyChanged(nameof(SessionManagerToolTip));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(ConnectToolTip));
        OnPropertyChanged(nameof(DisconnectText));
        OnPropertyChanged(nameof(DisconnectToolTip));
        OnPropertyChanged(nameof(SftpToolTip));
        OnPropertyChanged(nameof(MonitorText));
        OnPropertyChanged(nameof(MonitorToolTip));
        OnPropertyChanged(nameof(ThemeToolTip));
        OnPropertyChanged(nameof(FullScreenToolTip));
        OnPropertyChanged(nameof(ArrangeText));
        OnPropertyChanged(nameof(ArrangeToolTip));
        OnPropertyChanged(nameof(LanguageToolTip));
        OnPropertyChanged(nameof(AddQuickSessionToolTip));
        OnPropertyChanged(nameof(ArrangeVerticalText));
        OnPropertyChanged(nameof(ArrangeHorizontalText));
        OnPropertyChanged(nameof(ArrangeTileText));
        OnPropertyChanged(nameof(ArrangeMergeText));
        OnPropertyChanged(nameof(QuickPropertiesText));
        OnPropertyChanged(nameof(QuickDeleteText));
        OnPropertyChanged(nameof(TabCloseText));
        OnPropertyChanged(nameof(TabPropertiesText));
        OnPropertyChanged(nameof(TabAddQuickText));
        OnPropertyChanged(nameof(WelcomeSelectSessionText));
        OnPropertyChanged(nameof(WelcomeBuiltWithAtomUiText));
        OnPropertyChanged(nameof(FullScreenEscBackText));
        OnPropertyChanged(nameof(ChineseLanguageText));
        OnPropertyChanged(nameof(EnglishLanguageText));
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsSelected = tab == value;

        ActivateTabGroupForSelectedTab(value);

        NotifySelectedContentVisibilityChanged();
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
        MergeTabGroupsCommand.NotifyCanExecuteChanged();
    }

    private void NotifySelectedContentVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsSelectedTerminalSession));
        OnPropertyChanged(nameof(IsSelectedVncSession));
        OnPropertyChanged(nameof(IsSelectedRdpSession));
        OnPropertyChanged(nameof(IsSelectedFileTransferSession));
    }

    partial void OnIsMonitorVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMonitorPanelVisible));
        if (value)
            UpdateMonitor(SelectedTab);
        else
            Monitor.StopMonitoring();
    }

    partial void OnIsSftpVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSftpPanelVisible));
        OnPropertyChanged(nameof(SftpSplitterWidth));
        if (value)
        {
            if (!IsTerminalFullScreen)
                RestoreSftpPanelWidth(resetToDefault: true);
            UpdateSftp(SelectedTab);
        }
        else
        {
            CollapseSftpPanelWidth();
            Sftp.StopBrowsing();
        }
    }

    partial void OnIsTerminalFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMainChromeVisible));
        OnPropertyChanged(nameof(IsSftpPanelVisible));
        OnPropertyChanged(nameof(SftpSplitterWidth));
        OnPropertyChanged(nameof(IsMonitorPanelVisible));
        OnPropertyChanged(nameof(IsTabHeaderVisible));
        OnPropertyChanged(nameof(IsMainTabHeaderVisible));
        OnPropertyChanged(nameof(IsSingleTabContentVisible));
        OnPropertyChanged(nameof(IsArrangedTabsVisible));
        if (value)
            CollapseSftpPanelWidth();
        else if (IsSftpVisible)
            RestoreSftpPanelWidth(resetToDefault: true);
        IsFullScreenHintVisible = value;
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
        if (!IsMonitorVisible) return;
        if (tab == null || tab.Vnc != null || tab.Rdp != null || !tab.Terminal.IsConnected || tab.Session.Protocol != SessionProtocol.SSH)
        {
            Monitor.StopMonitoring();
            return;
        }
        Monitor.SwitchConnection(tab.Session, tab.ConnectedPassword);
    }

    private void UpdateSftp(TerminalTabViewModel? tab)
    {
        if (!IsSftpVisible) return;
        if (tab == null ||
            tab.Vnc != null ||
            tab.FileTransfer != null ||
            !tab.Terminal.IsConnected ||
            tab.Session.Protocol != SessionProtocol.SSH)
        {
            Sftp.StopBrowsing();
            return;
        }
        Sftp.SwitchConnection(tab.Session, tab.ConnectedPassword);
    }

    private double _lastSftpPanelWidth = DefaultSftpPanelWidth;

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
        _sessionManagerWindow.Closed += (_, _) => _sessionManagerWindow = null;
        _sessionManagerWindow.Show(owner);
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

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }

        if (vm.SavedSession != null)
        {
            _sessionTreeVm.AddSession(vm.SavedSession);
            if (dialog.ShouldConnect)
            {
                CloseSessionManagerWindow();
                await ConnectSession(vm.SavedSession);
            }
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

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }

        if (vm.SavedSession != null)
        {
            _sessionTreeVm.UpdateSession(vm.SavedSession);
            RefreshOpenTabsForSession(vm.SavedSession);
            if (dialog.ShouldConnect)
            {
                CloseSessionManagerWindow();
                await ConnectSession(vm.SavedSession);
            }
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
        }
    }

    public void DeleteSession(SessionInfo session)
    {
        _sessionTreeVm.DeleteSession(session);
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

    private void CloseSessionManagerWindow()
    {
        var window = _sessionManagerWindow;
        if (window == null)
            return;

        _sessionManagerWindow = null;
        window.Close();
    }

    public async Task ConnectSession(SessionInfo session)
    {
        if (session.Protocol is SessionProtocol.SFTP or SessionProtocol.FTP)
        {
            await ConnectFileTransferSession(session);
            return;
        }

        if (session.Protocol == SessionProtocol.RDP)
        {
            await ConnectRdpSession(session);
            return;
        }

        if (session.Protocol == SessionProtocol.VNC)
        {
            await ConnectVncSession(session);
            return;
        }

        if (session.Protocol is not (SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN or SessionProtocol.SERIAL))
        {
            ConnectionStatusText = $"Protocol {session.Protocol} does not support terminal connection yet";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            return;
        }

        string? password = GetSavedPassword(session);

        if (session.Protocol is SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN &&
            string.IsNullOrEmpty(password) &&
            SshAgentAuthService.ShouldPromptForPassword(session))
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
                return;
        }

        var tab = new TerminalTabViewModel(session);
        tab.CloseRequested += CloseTab;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;

        tab.Terminal.PropertyChanged += OnActiveTerminalPropertyChanged;

        try
        {
            ConnectionStatusText = "Connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));

            await tab.Terminal.ConnectAsync(session, password);

            tab.ConnectedPassword = password;

            if (IsMonitorVisible && tab.Session.Protocol == SessionProtocol.SSH)
                Monitor.SwitchConnection(tab.Session, tab.ConnectedPassword);

            if (IsSftpVisible && tab.Session.Protocol == SessionProtocol.SSH)
                Sftp.SwitchConnection(tab.Session, tab.ConnectedPassword);
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Connection failed: {ex.Message}";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
        }
    }

    private async Task ConnectRdpSession(SessionInfo session)
    {
        var password = GetSavedPassword(session);
        if (string.IsNullOrEmpty(password))
            password = await ShowPasswordDialog(session);

        if (password == null)
            return;

        var rdp = new RdpViewModel(session, password);
        var tab = new TerminalTabViewModel(session, rdp);
        tab.CloseRequested += CloseTab;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;

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
        return $"{host}:{port} via SSH {sshHost}:{sshPort}";
    }

    private async Task ConnectVncSession(SessionInfo session)
    {
        var password = GetSavedPassword(session);
        if (string.IsNullOrEmpty(password))
            password = await ShowPasswordDialog(session);
        if (password == null)
            return;

        var vm = new VncViewModel();
        var tab = new TerminalTabViewModel(session, vm);
        tab.CloseRequested += CloseTab;
        AddTabToActiveGroup(tab);
        SelectedTab = tab;
        IsSftpVisible = false;
        IsMonitorVisible = false;

        try
        {
            ConnectionStatusText = "VNC connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            ConnectedHostInfo = BuildVncHostInfo(session);
            await vm.ConnectAsync(session, password);
            ConnectionStatusText = "VNC connected";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
            UpdateTerminalSize();
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"VNC failed: {ex.Message}";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
            vm.StatusText = $"VNC failed: {ex.Message}";
        }
    }

    private async Task ConnectFileTransferSession(SessionInfo session)
    {
        string? password = GetSavedPassword(session);

        if (string.IsNullOrEmpty(password) && SshAgentAuthService.ShouldPromptForPassword(session))
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
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
        AddTabToActiveGroup(tab);
        SelectedTab = tab;

        var connected = await fileTransfer.SwitchConnectionAsync(session, password);
        tab.ConnectedPassword = password;
        if (connected)
        {
            ConnectionStatusText = $"{session.Protocol} connected";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
        }
        else
        {
            ConnectionStatusText = $"{session.Protocol} connection failed";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
        }
    }

    private void OnActiveTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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

    public void CloseTab(TerminalTabViewModel tab)
    {
        var wasSelected = SelectedTab == tab;
        var group = IsTabArrangementEnabled ? FindTabGroup(tab) : null;

        tab.Terminal.PropertyChanged -= OnActiveTerminalPropertyChanged;

        group?.RemoveTab(tab);
        if (group is { HasTabs: false })
            TabGroups.Remove(group);

        Tabs.Remove(tab);
        CleanupClosedTabResources(tab);

        if (wasSelected && Tabs.Count > 0)
        {
            SelectedTab = group?.SelectedTab ?? SelectedTabGroup?.SelectedTab ?? Tabs.Last();
        }

        if (Tabs.Count == 0)
        {
            IsTerminalFullScreen = false;
            TabGroups.Clear();
            SetSelectedTabGroup(null);
            Monitor.StopMonitoring();
            Sftp.StopBrowsing();
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

    private static void CleanupClosedTabResources(TerminalTabViewModel tab)
    {
        tab.Terminal.CloseDetached();
        if (tab.Vnc != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    tab.Vnc.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"VNC close cleanup failed: {ex.Message}");
                }
            });
        }
        tab.Rdp?.Dispose();
        tab.FileTransfer?.StopBrowsing();
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
            SelectedTab?.Terminal.Disconnect();
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

            ConnectionStatusText = $"{tab.Session.Protocol} connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            await tab.FileTransfer.SwitchConnectionAsync(tab.Session, filePassword);
            tab.ConnectedPassword = filePassword;
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

        try
        {
            ConnectionStatusText = "Connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            await tab.Terminal.ConnectAsync(tab.Session, password);
            tab.ConnectedPassword = password;
            UpdateMonitor(tab);
            UpdateSftp(tab);
        }
        catch (Exception ex)
        {
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
            SelectedTab?.Terminal.Disconnect("[Current session disconnected]");

        Monitor.StopMonitoring();
        Sftp.StopBrowsing();
        UpdateStatusBar();
        UpdateTerminalSize();
    }

    private static string? GetSavedPassword(SessionInfo session)
    {
        var password = PasswordEncryptionService.Decrypt(session.Password);
        return string.IsNullOrEmpty(password) ? null : password;
    }

    private async Task<string?> ShowPasswordDialog(SessionInfo session)
    {
        var dialog = new AtomUI.Desktop.Controls.Window
        {
            Title = string.Format(_localization.Text("PasswordDialog.Title"), session.Name),
            Width = 460,
            Height = 250,
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
            SizeType = SizeType.Middle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 34,
            Margin = new Thickness(20, 10)
        };

        string? result = null;

        void Confirm()
        {
            result = passwordBox.Text;
            dialog.Close();
        }

        var okButton = new AtomUI.Desktop.Controls.Button
        {
            Content = _localization.Text("PasswordDialog.Ok"),
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Primary,
            SizeType = SizeType.Middle
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
            SizeType = SizeType.Middle
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(_localization.Text("PasswordDialog.User"), session.Username, session.Host, session.Port),
            Margin = new Thickness(20, 20, 20, 0)
        });
        panel.Children.Add(passwordBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 0)
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

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

        dialog.Content = panel;
        dialog.Opened += (_, _) => passwordBox.Focus();

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }

        return result;
    }
}
