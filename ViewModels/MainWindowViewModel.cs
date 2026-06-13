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

    public ServerMonitorViewModel Monitor { get; } = new();
    public SftpViewModel Sftp { get; } = new();
    public ObservableCollection<SessionInfo> QuickSessions => _sessionTreeVm.QuickSessions;
    public string ThemeIcon => IsDarkMode ? "\u263E" : "\u2600";

    // 婵炴潙鍚嬫穱娲儊閻ｅ瞼涓嶉柨娑樺閸婄偤鏌涢敐鍐ㄥ闁绘稏鍎靛畷锝夋晲閸涱厾顦版繛鎾磋壘椤戞垹妲愬┑瀣劶闁割煈鍠栫敮鎶芥⒑閹绘帞孝妞わ附鐓￠獮宥夊箻瀹曞洨锛?
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
        UpdateStatusBar();
        UpdateTerminalSize();
        UpdateMonitor(value);
        UpdateSftp(value);
        CurrentConnectCommand.NotifyCanExecuteChanged();
        CurrentDisconnectCommand.NotifyCanExecuteChanged();
        AddCurrentSessionToQuickBarCommand.NotifyCanExecuteChanged();
        ToggleTerminalFullScreenCommand.NotifyCanExecuteChanged();
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
        if (tab == null || !tab.Terminal.IsConnected || tab.Session.Protocol != SessionProtocol.SSH)
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
            !tab.Terminal.IsConnected ||
            tab.Session.Protocol != SessionProtocol.SSH ||
            tab.Session.SshDoNotStartFileManager)
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

        // 婵犵鈧啿鈧綊鎮樻径宀€鐜绘俊銈傚亾鐟滅増绋戦蹇涘箵閹烘梹鎲奸梺闈╄礋閸斿绮弽顓炲珘妞ゅ繐瀚ぐ鐘绘⒒閸屻倕寮跨紒杈ㄧ箞瀹曟艾鈻庨幇顔跨濠电偠灏褔鎮?
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
        var vm = new SessionEditViewModel();
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
        if (session.Protocol is SessionProtocol.SFTP or SessionProtocol.FTP)
        {
            await ConnectFileTransferSession(session);
            return;
        }

        if (session.Protocol == SessionProtocol.RDP)
        {
            ConnectRdpSession(session);
            return;
        }

        if (session.Protocol is not (SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN or SessionProtocol.SERIAL or SessionProtocol.LOCAL))
        {
            ConnectionStatusText = $"Protocol {session.Protocol} does not support terminal connection yet";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            return;
        }

        string? password = null;

        if (session.Protocol is SessionProtocol.SSH or SessionProtocol.TELNET or SessionProtocol.RLOGIN &&
            SshAgentAuthService.ShouldPromptForPassword(session))
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
                return;
        }

        var tab = new TerminalTabViewModel(session);
        tab.CloseRequested += CloseTab;
        Tabs.Add(tab);
        SelectedTab = tab;

        // 闁烩晜鍨甸幆澶庛亹閹惧啿顤呴梺顐㈩槷閼?Tab 闁?Terminal 闁告瑦锚鐎靛弶绂掗妷锔界函闁哄倹澹嗘慨鎼佸箑娴ｅ湱鍩?
        tab.Terminal.PropertyChanged += OnActiveTerminalPropertyChanged;

        try
        {
            ConnectionStatusText = "Connecting...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));

            await tab.Terminal.ConnectAsync(session, password);

            // 濞ｅ洦绻傞悺銊р偓闈涙閻栨粍绗熷☉姘＇闁硅矇鍛仾缂佹柨顑堢换娑㈠箳閵夈倕鈻忛柣?
            tab.ConnectedPassword = password;

            // 濠碘€冲€归悘澶愭儎閹寸偛浠橀梻鍫涘灪濠㈡ê顔忛崣澶娾叺鐎殿喒鍋撻柨娑樼灱閻濇盯宕￠崘鍙夊剻闁告柣鍔庡ú鍐箳?
            if (IsMonitorVisible && tab.Session.Protocol == SessionProtocol.SSH)
                Monitor.SwitchConnection(tab.Session, tab.ConnectedPassword);

            // 濠碘€冲€归悘?SFTP 闂傚牄鍨哄妯侯啅閸欏鈪电€殿喒鍋撻柨娑樼灱閻濇盯宕＄€圭姷绠鹃柟?
            if (IsSftpVisible && tab.Session.Protocol == SessionProtocol.SSH && !tab.Session.SshDoNotStartFileManager)
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
        return SelectedTab != null && !SelectedTab.Terminal.IsConnected;
    }

    [RelayCommand(CanExecute = nameof(CanCurrentConnect))]
    private async Task CurrentConnect()
    {
        var tab = SelectedTab;
        if (tab == null || tab.Terminal.IsConnected)
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
        return SelectedTab?.Terminal.IsConnected == true;
    }

    [RelayCommand(CanExecute = nameof(CanCurrentDisconnect))]
    private void CurrentDisconnect()
    {
        SelectedTab?.Terminal.Disconnect("[Current session disconnected]");
        Monitor.StopMonitoring();
        Sftp.StopBrowsing();
    }

    private async Task<string?> ShowPasswordDialog(SessionInfo session)
    {
        var dialog = new Window
        {
            Title = $"Enter password - {session.Name}",
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
            PlaceholderText = "Enter password",
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
            Content = "OK",
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
            Content = "Cancel",
            Width = 86,
            ButtonType = AtomUI.Desktop.Controls.ButtonType.Default,
            SizeType = SizeType.Middle
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"User: {session.Username}@{session.Host}:{session.Port}",
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
