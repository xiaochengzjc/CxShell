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
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using ChiXueSsh.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly SessionTreeViewModel _sessionTreeVm;

    [ObservableProperty] private SessionTreeViewModel _sessionTree;
    [ObservableProperty] private bool _isMonitorVisible = false;
    [ObservableProperty] private bool _isSftpVisible = false;
    [ObservableProperty] private string _connectionStatusText = "Disconnected";
    [ObservableProperty] private IBrush _connectionStatusColor = Brushes.Gray;
    [ObservableProperty] private string _connectedHostInfo = string.Empty;
    [ObservableProperty] private string _terminalSizeText = "80x24";
    [ObservableProperty] private bool _isDarkMode;
    [ObservableProperty] private bool _isTerminalFullScreen;
    [ObservableProperty] private bool _isFullScreenHintVisible;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    [ObservableProperty] private TerminalTabViewModel? _selectedTab;

    public bool HasTabs => Tabs.Count > 0;
    public bool IsMainChromeVisible => !IsTerminalFullScreen;
    public bool IsSftpPanelVisible => IsSftpVisible && !IsTerminalFullScreen;
    public bool IsMonitorPanelVisible => IsMonitorVisible && !IsTerminalFullScreen;
    public bool IsTabHeaderVisible => !IsTerminalFullScreen;
    public bool IsSelectedTerminalSession => SelectedTab?.IsTerminalSession == true;
    public bool IsSelectedVncSession => SelectedTab?.IsVncSession == true;

    public ServerMonitorViewModel Monitor { get; } = new();
    public SftpViewModel Sftp { get; } = new();
    public ObservableCollection<SessionInfo> QuickSessions => _sessionTreeVm.QuickSessions;
    public string ThemeIcon => IsDarkMode ? "\u263E" : "\u2600";

    private Window? _sessionManagerWindow;

    public MainWindowViewModel()
    {
        _sessionTreeVm = new SessionTreeViewModel(this);
        _sessionTree = _sessionTreeVm;

        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            ToggleTerminalFullScreenCommand.NotifyCanExecuteChanged();
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

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        foreach (var tab in Tabs)
            tab.IsSelected = tab == value;
        NotifySelectedContentVisibilityChanged();
        if (value?.Vnc != null)
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

    private void NotifySelectedContentVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsSelectedTerminalSession));
        OnPropertyChanged(nameof(IsSelectedVncSession));
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
        if (value)
            UpdateSftp(SelectedTab);
        else
            Sftp.StopBrowsing();
    }

    partial void OnIsTerminalFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMainChromeVisible));
        OnPropertyChanged(nameof(IsSftpPanelVisible));
        OnPropertyChanged(nameof(IsMonitorPanelVisible));
        OnPropertyChanged(nameof(IsTabHeaderVisible));
        IsFullScreenHintVisible = value;
    }

    private void UpdateMonitor(TerminalTabViewModel? tab)
    {
        if (!IsMonitorVisible) return;
        if (tab == null || tab.Vnc != null || !tab.Terminal.IsConnected || tab.Session.Protocol != SessionProtocol.SSH)
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
            !tab.Terminal.IsConnected ||
            tab.Session.Protocol != SessionProtocol.SSH)
        {
            Sftp.StopBrowsing();
            return;
        }
        Sftp.SwitchConnection(tab.Session, tab.ConnectedPassword);
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
        var vm = new SessionEditViewModel(_sessionTreeVm.CreateSessionFromGlobalDefaults());
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
            if (dialog.ShouldConnect)
            {
                CloseSessionManagerWindow();
                await ConnectSession(vm.SavedSession);
            }
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
        var effectiveSession = _sessionTreeVm.GetEffectiveSession(session);

        if (effectiveSession.Protocol is SessionProtocol.SFTP or SessionProtocol.FTP)
        {
            await ConnectFileTransferSession(effectiveSession);
            return;
        }

        if (effectiveSession.Protocol == SessionProtocol.RDP)
        {
            ConnectRdpSession(effectiveSession);
            return;
        }

        if (effectiveSession.Protocol == SessionProtocol.VNC)
        {
            await ConnectVncSession(effectiveSession);
            return;
        }

        if (effectiveSession.Protocol is not (SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN or SessionProtocol.SERIAL))
        {
            ConnectionStatusText = $"Protocol {effectiveSession.Protocol} does not support terminal connection yet";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            return;
        }

        string? password = null;

        if (effectiveSession.Protocol is SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN &&
            SshAgentAuthService.ShouldPromptForPassword(effectiveSession))
        {
            password = await ShowPasswordDialog(effectiveSession);
            if (password == null)
                return;
        }

        var tab = new TerminalTabViewModel(effectiveSession);
        tab.CloseRequested += CloseTab;
        Tabs.Add(tab);
        SelectedTab = tab;

        tab.Terminal.PropertyChanged += OnActiveTerminalPropertyChanged;

        try
        {
            ConnectionStatusText = "Connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));

            await tab.Terminal.ConnectAsync(effectiveSession, password);

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

    private void ConnectRdpSession(SessionInfo session)
    {
        try
        {
            var filePath = RdpLaunchService.Launch(session);
            IsTerminalFullScreen = false;
            ConnectionStatusText = "RDP launched";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
            ConnectedHostInfo = $"{session.Username}@{session.Host}:{session.Port}";
            System.Diagnostics.Debug.WriteLine($"RDP file: {filePath}");
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"RDP launch failed: {ex.Message}";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FF4D4F"));
        }
    }

    private async Task ConnectVncSession(SessionInfo session)
    {
        var password = await ShowPasswordDialog(session);
        if (password == null)
            return;

        var vm = new VncViewModel();
        var tab = new TerminalTabViewModel(session, vm);
        tab.CloseRequested += CloseTab;
        Tabs.Add(tab);
        SelectedTab = tab;
        IsSftpVisible = false;
        IsMonitorVisible = false;

        try
        {
            ConnectionStatusText = "VNC connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            ConnectedHostInfo = $"{session.Host}:{(session.Port > 0 ? session.Port : 5900)}";
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
        string? password = null;

        if (SshAgentAuthService.ShouldPromptForPassword(session))
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
                return;
        }

        IsTerminalFullScreen = false;
        IsSftpVisible = true;
        ConnectionStatusText = $"{session.Protocol} connecting...";
        ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
        ConnectedHostInfo = $"{session.Username}@{session.Host}:{session.Port}";

        var connected = await Sftp.SwitchConnectionAsync(session, password);
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
        tab.Terminal.PropertyChanged -= OnActiveTerminalPropertyChanged;
        tab.Terminal.Disconnect();
        tab.Vnc?.Dispose();
        Tabs.Remove(tab);

        if (SelectedTab == null && Tabs.Count > 0)
        {
            SelectedTab = Tabs.Last();
        }

        if (Tabs.Count == 0)
        {
            IsTerminalFullScreen = false;
            Monitor.StopMonitoring();
            Sftp.StopBrowsing();
            UpdateStatusBar();
            UpdateTerminalSize();
        }
    }

    [RelayCommand]
    private void SelectTab(TerminalTabViewModel tab)
    {
        SelectedTab = tab;
    }

    [RelayCommand]
    private void Disconnect()
    {
        SelectedTab?.Terminal.Disconnect();
    }

    private bool CanCurrentConnect()
    {
        return SelectedTab != null &&
               (SelectedTab.Vnc != null
                   ? !SelectedTab.Vnc.IsConnected
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

            var vncPassword = tab.ConnectedPassword ?? await ShowPasswordDialog(tab.Session);
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

        if (tab.Terminal.IsConnected)
            return;

        var password = tab.ConnectedPassword;
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
        return SelectedTab?.Vnc?.IsConnected == true || SelectedTab?.Terminal.IsConnected == true;
    }

    [RelayCommand(CanExecute = nameof(CanCurrentDisconnect))]
    private void CurrentDisconnect()
    {
        if (SelectedTab?.Vnc != null)
            SelectedTab.Vnc.Disconnect();
        else
            SelectedTab?.Terminal.Disconnect("[Current session disconnected]");

        Monitor.StopMonitoring();
        Sftp.StopBrowsing();
        UpdateStatusBar();
        UpdateTerminalSize();
    }

    private async Task<string?> ShowPasswordDialog(SessionInfo session)
    {
        var dialog = new AtomUI.Desktop.Controls.Window
        {
            Title = $"输入密码 - {session.Name}",
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
            PlaceholderText = "请输入密码",
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
            Content = "确定",
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
            Content = "取消",
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Default,
            SizeType = SizeType.Middle
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"用户: {session.Username}@{session.Host}:{session.Port}",
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
