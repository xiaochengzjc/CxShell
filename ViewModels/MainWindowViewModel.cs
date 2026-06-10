using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AtomUI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using ChiXueSsh.Models;
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
    [ObservableProperty] private string _connectionStatusText = "未连接";
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
    public string ThemeIcon => IsDarkMode ? "☾" : "☀";

    // 会话管理器窗口单例，避免重复打开
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

        // 初始化主题状态
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
        if (tab == null || !tab.Terminal.IsConnected)
        {
            Monitor.StopMonitoring();
            return;
        }
        Monitor.SwitchConnection(tab.Session, tab.ConnectedPassword);
    }

    private void UpdateSftp(TerminalTabViewModel? tab)
    {
        if (!IsSftpVisible) return;
        if (tab == null || !tab.Terminal.IsConnected)
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

        // 如果窗口已存在且未关闭，则激活它
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
            ConnectionStatusText = "已连接";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#52C41A"));
            ConnectedHostInfo = terminal.HostInfo;
        }
        else
        {
            ConnectionStatusText = "未连接";
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
        }
    }

    public void DeleteSession(SessionInfo session)
    {
        _sessionTreeVm.DeleteSession(session);
    }

    public async Task ConnectSession(SessionInfo session)
    {
        string? password = null;

        if (session.AuthMethod == AuthMethod.Password)
        {
            password = await ShowPasswordDialog(session);
            if (password == null)
                return;
        }

        var tab = new TerminalTabViewModel(session);
        tab.CloseRequested += CloseTab;
        Tabs.Add(tab);
        SelectedTab = tab;

        // 监听当前选中 Tab 的 Terminal 变化以更新状态栏
        tab.Terminal.PropertyChanged += OnActiveTerminalPropertyChanged;

        try
        {
            ConnectionStatusText = "连接中...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));

            await tab.Terminal.ConnectAsync(session, password);

            // 保存密码供监控独立连接使用
            tab.ConnectedPassword = password;

            // 如果监控面板已打开，立即启动监控
            if (IsMonitorVisible)
                Monitor.SwitchConnection(tab.Session, tab.ConnectedPassword);

            // 如果 SFTP 面板已打开，立即连接
            if (IsSftpVisible)
                Sftp.SwitchConnection(tab.Session, tab.ConnectedPassword);
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"连接失败: {ex.Message}";
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
        if (tab.Session.AuthMethod == AuthMethod.Password && password == null)
        {
            password = await ShowPasswordDialog(tab.Session);
            if (password == null)
                return;
        }

        try
        {
            ConnectionStatusText = "连接中...";
            ConnectionStatusColor = new SolidColorBrush(Color.Parse("#FAAD14"));
            await tab.Terminal.ConnectAsync(tab.Session, password);
            tab.ConnectedPassword = password;
            UpdateMonitor(tab);
            UpdateSftp(tab);
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"连接失败: {ex.Message}";
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
        SelectedTab?.Terminal.Disconnect("[当前会话已断开]");
        Monitor.StopMonitoring();
        Sftp.StopBrowsing();
    }

    private async Task<string?> ShowPasswordDialog(SessionInfo session)
    {
        var dialog = new Window
        {
            Title = $"输入密码 - {session.Name}",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var passwordBox = new TextBox
        {
            PasswordChar = '*',
            PlaceholderText = "请输入密码",
            Margin = new Thickness(20, 10)
        };

        string? result = null;

        var okButton = new Button { Content = "确定", Width = 80 };
        okButton.Click += (_, _) =>
        {
            result = passwordBox.Text;
            dialog.Close();
        };

        var cancelButton = new Button { Content = "取消", Width = 80 };
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
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(20, 0)
        };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow != null)
        {
            await dialog.ShowDialog(lifetime.MainWindow);
        }

        return result;
    }
}
