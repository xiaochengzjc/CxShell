using System;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class MainWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);
    private readonly DispatcherTimer _fullScreenHintTimer;
    private readonly string[] _startupArgs;
    private bool _isPointerOverFullScreenHintArea;
    private SessionInfo? _quickSessionContext;
    private TerminalTabViewModel? _tabContext;

    public MainWindow()
        : this(Array.Empty<string>())
    {
    }

    public MainWindow(string[] startupArgs)
    {
        _startupArgs = startupArgs;
        InitializeComponent();
        _fullScreenHintTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _fullScreenHintTimer.Tick += (_, _) => HideFullScreenHintIfNeeded();

        var vm = new MainWindowViewModel();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsTerminalFullScreen))
            {
                WindowState = vm.IsTerminalFullScreen
                    ? Avalonia.Controls.WindowState.FullScreen
                    : Avalonia.Controls.WindowState.Normal;
                _isPointerOverFullScreenHintArea = false;
                if (vm.IsTerminalFullScreen)
                    RestartFullScreenHintTimer();
                else
                    _fullScreenHintTimer.Stop();
            }
        };
        DataContext = vm;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QuickSessionPropertiesBtn.Click += OnQuickSessionPropertiesClick;
        QuickSessionDeleteBtn.Click += OnQuickSessionDeleteClick;
        TabCloseBtn.Click += OnTabCloseClick;
        TabPropertiesBtn.Click += OnTabPropertiesClick;
        TabAddQuickBtn.Click += OnTabAddQuickClick;
        StartRdpSmokeIfRequested();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        QuickSessionContextMenuPopup.Close();
        TabContextMenuPopup.Close();
        QuickSessionPropertiesBtn.Click -= OnQuickSessionPropertiesClick;
        QuickSessionDeleteBtn.Click -= OnQuickSessionDeleteClick;
        TabCloseBtn.Click -= OnTabCloseClick;
        TabPropertiesBtn.Click -= OnTabPropertiesClick;
        TabAddQuickBtn.Click -= OnTabAddQuickClick;
        base.OnUnloaded(e);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
        {
            vm.ExitTerminalFullScreen();
            e.Handled = true;
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            e.Source is not Avalonia.Controls.Control source)
        {
            return;
        }

        Avalonia.Controls.Control? current = source;
        while (current != null)
        {
            if (current.DataContext is TerminalTabViewModel tab)
            {
                vm.SelectTabCommand.Execute(tab);
                return;
            }

            if (current.DataContext is TerminalTabGroupViewModel group)
            {
                vm.SelectTabGroupCommand.Execute(group);
                return;
            }

            current = current.Parent as Avalonia.Controls.Control;
        }
    }

    private void OnQuickSessionTagPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (sender is Avalonia.Controls.Control { DataContext: SessionInfo session })
        {
            _quickSessionContext = session;
            QuickSessionContextMenuPopup.PlacementTarget = sender as Avalonia.Controls.Control;
            QuickSessionContextMenuPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnTabHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (sender is Avalonia.Controls.Control { DataContext: TerminalTabViewModel tab } &&
            DataContext is MainWindowViewModel vm)
        {
            _tabContext = tab;
            vm.SelectTabCommand.Execute(tab);
            UpdateTabAddQuickMenuState(vm.AddCurrentSessionToQuickBarCommand.CanExecute(null));
            TabContextMenuPopup.PlacementTarget = sender as Avalonia.Controls.Control;
            TabContextMenuPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private void UpdateTabAddQuickMenuState(bool canAdd)
    {
        TabAddQuickBtn.IsEnabled = canAdd;
        TabAddQuickBtn.Opacity = canAdd ? 1.0 : 0.42;
        TabAddQuickBtn.Foreground = canAdd
            ? Brushes.Black
            : Brushes.Gray;
    }

    private void OnTabArrangeButtonClick(object? sender, RoutedEventArgs e)
    {
        TabArrangePopup.PlacementTarget = TabArrangeButton;
        TabArrangePopup.IsOpen = true;
    }

    private void OnTabArrangeMenuItemClick(object? sender, RoutedEventArgs e)
    {
        TabArrangePopup.Close();
    }

    private void OnLanguageButtonClick(object? sender, RoutedEventArgs e)
    {
        LanguagePopup.PlacementTarget = LanguageButton;
        LanguagePopup.IsOpen = true;
    }

    private void OnLanguageMenuItemClick(object? sender, RoutedEventArgs e)
    {
        LanguagePopup.Close();
    }

    private void OnTabGroupPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: TerminalTabGroupViewModel group } &&
            DataContext is MainWindowViewModel vm)
        {
            vm.SelectTabGroupCommand.Execute(group);
        }
    }

    private async void OnQuickSessionPropertiesClick(object? sender, RoutedEventArgs e)
    {
        QuickSessionContextMenuPopup.Close();
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_quickSessionContext);
    }

    private void OnQuickSessionDeleteClick(object? sender, RoutedEventArgs e)
    {
        QuickSessionContextMenuPopup.Close();
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.RemoveQuickSessionCommand.Execute(_quickSessionContext);
        _quickSessionContext = null;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        TabContextMenuPopup.Close();
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.CloseTab(_tabContext);
        _tabContext = null;
    }

    private async void OnTabPropertiesClick(object? sender, RoutedEventArgs e)
    {
        TabContextMenuPopup.Close();
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_tabContext.Session);
    }

    private void OnTabAddQuickClick(object? sender, RoutedEventArgs e)
    {
        TabContextMenuPopup.Close();
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectTabCommand.Execute(_tabContext);
        if (vm.AddCurrentSessionToQuickBarCommand.CanExecute(null))
            vm.AddCurrentSessionToQuickBarCommand.Execute(null);
    }

    private void StartRdpSmokeIfRequested()
    {
        if (Array.IndexOf(_startupArgs, "--rdp-smoke") < 0 ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            var host = GetStartupArg("--rdp-host") ?? "117.72.38.235";
            var port = int.TryParse(GetStartupArg("--rdp-port"), out var parsedPort) ? parsedPort : 3389;
            var username = GetStartupArg("--rdp-user") ?? "rdpuser";
            var password = GetStartupArg("--rdp-password") ?? string.Empty;
            var width = int.TryParse(GetStartupArg("--rdp-width"), out var parsedWidth) ? parsedWidth : 1280;
            var height = int.TryParse(GetStartupArg("--rdp-height"), out var parsedHeight) ? parsedHeight : 720;

            var session = new SessionInfo
            {
                Name = $"RDP Smoke {host}",
                Protocol = SessionProtocol.RDP,
                Host = host,
                Port = port,
                Username = username,
                AuthMethod = AuthMethod.Password,
                Password = PasswordEncryptionService.Encrypt(password),
                RdpWindowSize = "Custom",
                RdpDesktopWidth = width,
                RdpDesktopHeight = height
            };

            await vm.ConnectSession(session);
        });
    }

    private string? GetStartupArg(string name)
    {
        for (var index = 0; index < _startupArgs.Length - 1; index++)
        {
            if (string.Equals(_startupArgs[index], name, StringComparison.OrdinalIgnoreCase))
                return _startupArgs[index + 1];
        }

        return null;
    }

    private void FullScreenHintArea_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerOverFullScreenHintArea = true;
        _fullScreenHintTimer.Stop();

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
            vm.IsFullScreenHintVisible = true;
    }

    private void FullScreenHintArea_OnPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerOverFullScreenHintArea = false;
        RestartFullScreenHintTimer();
    }

    private void RestartFullScreenHintTimer()
    {
        _fullScreenHintTimer.Stop();

        if (DataContext is not MainWindowViewModel { IsTerminalFullScreen: true })
            return;

        _fullScreenHintTimer.Start();
    }

    private void HideFullScreenHintIfNeeded()
    {
        _fullScreenHintTimer.Stop();

        if (_isPointerOverFullScreenHintArea)
            return;

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
            vm.IsFullScreenHintVisible = false;
    }
}
