using System;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace CxShell.Views;

public partial class MainWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);
    private readonly DispatcherTimer _fullScreenHintTimer;
    private readonly string[] _startupArgs;
    private bool _isPointerOverFullScreenHintArea;
    private SessionInfo? _quickSessionContext;
    private TerminalTabViewModel? _tabContext;
    private bool _isDraggingSftpSplitter;
    private bool _isSftpPanelWidthApplyQueued;
    private bool _hasSftpSplitterPreviousCursor;
    private double _sftpSplitterStartX;
    private double _sftpSplitterStartWidth;
    private Cursor? _sftpSplitterPreviousCursor;

    private const double MinimumSftpPanelWidth = 120;
    private const double SftpSplitterHitSlop = 0;
    private const double MinimumTerminalPanelWidth = 320;
    private const double MonitorPanelWidth = 283;

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

            if (e.PropertyName == nameof(MainWindowViewModel.SftpPanelWidth) ||
                e.PropertyName == nameof(MainWindowViewModel.IsSftpVisible) ||
                e.PropertyName == nameof(MainWindowViewModel.IsTerminalFullScreen))
            {
                QueueApplySftpPanelWidth(vm);
            }
        };
        DataContext = vm;
        MainContentGrid.AddHandler(PointerPressedEvent, OnMainContentGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnSftpSplitterPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnSftpSplitterPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnSftpSplitterPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        QueueApplySftpPanelWidth(vm);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        StartRdpSmokeIfRequested();
        ShowSessionManagerOnStartupIfNeeded();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
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
            ShowQuickSessionContextMenu((Avalonia.Controls.Control)sender);
            e.Handled = true;
        }
    }

    private void OnTabHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tab = ResolveTabContext(sender as Avalonia.Controls.Control, out var anchor) ??
                  ResolveTabContext(e.Source as Avalonia.Controls.Control, out anchor);
        if (tab == null || anchor == null || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            vm.SelectTabCommand.Execute(tab);
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            _tabContext = tab;
            vm.SelectTabCommand.Execute(tab);
            ShowTabContextMenu(anchor, vm.AddCurrentSessionToQuickBarCommand.CanExecute(null));
            e.Handled = true;
        }
    }

    private void OnTabStripClosing(object? sender, TabStripClosingEventArgs e)
    {
        e.Cancel = true;

        var tab = e.TabStripItem.DataContext as TerminalTabViewModel ??
                  e.TabStripItem.Content as TerminalTabViewModel;
        if (tab != null && DataContext is MainWindowViewModel vm)
            vm.CloseTab(tab);
    }

    private static TerminalTabViewModel? ResolveTabContext(Avalonia.Controls.Control? source, out Avalonia.Controls.Control? anchor)
    {
        anchor = source;
        var current = source;
        while (current != null)
        {
            if (current.DataContext is TerminalTabViewModel tab)
            {
                anchor = current;
                return tab;
            }

            current = current.Parent as Avalonia.Controls.Control;
        }

        return null;
    }

    private void ShowQuickSessionContextMenu(Avalonia.Controls.Control anchor)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var menu = CreatePointerContextMenu(anchor);
        AddMenuItem(menu, vm.QuickPropertiesText, () => OnQuickSessionPropertiesClick(anchor, new RoutedEventArgs()));
        menu.Items.Add(new AtomMenuSeparator());
        AddMenuItem(menu, vm.QuickDeleteText, () => OnQuickSessionDeleteClick(anchor, new RoutedEventArgs()));
        menu.Open(anchor);
    }

    private void ShowTabContextMenu(Avalonia.Controls.Control anchor, bool canAddQuick)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var menu = CreatePointerContextMenu(anchor);
        AddMenuItem(menu, vm.TabCloseText, () => OnTabCloseClick(anchor, new RoutedEventArgs()));
        AddMenuItem(menu, vm.TabPropertiesText, () => OnTabPropertiesClick(anchor, new RoutedEventArgs()));
        AddMenuItem(menu, vm.TabAddQuickText, () => OnTabAddQuickClick(anchor, new RoutedEventArgs()), canAddQuick);
        menu.Open(anchor);
    }

    private static AtomContextMenu CreatePointerContextMenu(Avalonia.Controls.Control anchor)
    {
        return new AtomContextMenu
        {
            Placement = Avalonia.Controls.PlacementMode.Pointer,
            PlacementTarget = anchor
        };
    }

    private static void AddMenuItem(AtomContextMenu menu, string text, Action action, bool isEnabled = true)
    {
        var item = new AtomMenuItem
        {
            Header = text,
            IsEnabled = isEnabled
        };
        item.Click += (_, _) =>
        {
            menu.Close();
            action();
        };
        menu.Items.Add(item);
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

    private void OnSftpSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TryStartSftpSplitterDrag(sender, e);
    }

    private void OnMainContentGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.IsSftpPanelVisible ||
            !IsLeftButtonPress(e))
        {
            return;
        }

        var position = e.GetPosition(MainContentGrid);
        var splitterLeft = SftpPanelHost.Bounds.Right;
        var splitterRight = splitterLeft + Math.Max(vm.SftpSplitterWidth.Value, 1);
        if (position.X < splitterLeft - SftpSplitterHitSlop ||
            position.X > splitterRight + SftpSplitterHitSlop)
        {
            return;
        }

        TryStartSftpSplitterDrag(MainContentGrid, e);
    }

    private void TryStartSftpSplitterDrag(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !vm.IsSftpPanelVisible ||
            !IsLeftButtonPress(e))
        {
            return;
        }

        if (_isDraggingSftpSplitter)
            return;

        _isDraggingSftpSplitter = true;
        _sftpSplitterStartX = e.GetPosition(MainContentGrid).X;
        _sftpSplitterStartWidth = Math.Max(MinimumSftpPanelWidth, vm.SftpPanelWidth.Value);

        e.Pointer.Capture(this);
        ShowSftpSplitterCursor();

        e.Handled = true;
    }

    private void OnSftpSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingSftpSplitter ||
            DataContext is not MainWindowViewModel vm ||
            !vm.IsSftpPanelVisible)
        {
            return;
        }

        var delta = e.GetPosition(MainContentGrid).X - _sftpSplitterStartX;
        var maxWidth = GetMaximumSftpPanelWidth(vm);
        var width = Math.Min(Math.Max(MinimumSftpPanelWidth, _sftpSplitterStartWidth + delta), maxWidth);
        vm.SftpPanelWidth = new Avalonia.Controls.GridLength(width);
        ShowSftpSplitterCursor();
        e.Handled = true;
    }

    private void OnSftpSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSftpSplitter)
            return;

        EndSftpSplitterDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnSftpSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Source, this))
            EndSftpSplitterDrag(null);
    }

    private void EndSftpSplitterDrag(IPointer? pointer)
    {
        _isDraggingSftpSplitter = false;
        pointer?.Capture(null);
        ClearSftpSplitterCursor();
    }

    private void ShowSftpSplitterCursor()
    {
        if (!_hasSftpSplitterPreviousCursor)
        {
            _sftpSplitterPreviousCursor = Cursor;
            _hasSftpSplitterPreviousCursor = true;
        }

        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    private void ClearSftpSplitterCursor()
    {
        if (!_hasSftpSplitterPreviousCursor)
            return;

        Cursor = _sftpSplitterPreviousCursor;
        _sftpSplitterPreviousCursor = null;
        _hasSftpSplitterPreviousCursor = false;
    }

    private bool IsLeftButtonPress(PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        return properties.IsLeftButtonPressed ||
               properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
    }

    private void QueueApplySftpPanelWidth(MainWindowViewModel vm)
    {
        if (_isSftpPanelWidthApplyQueued)
            return;

        _isSftpPanelWidthApplyQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isSftpPanelWidthApplyQueued = false;
            ApplySftpPanelWidth(vm);
        }, DispatcherPriority.Render);
    }

    private void ApplySftpPanelWidth(MainWindowViewModel vm)
    {
        var width = vm.IsSftpPanelVisible
            ? Math.Max(MinimumSftpPanelWidth, vm.SftpPanelWidth.Value)
            : 0;

        MainContentGrid.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(width);
        SftpPanelHost.Width = width;
        SftpPanelHost.MinWidth = width > 0 ? MinimumSftpPanelWidth : 0;
        SftpPanelHost.MaxWidth = width;
        SftpPanelHost.InvalidateMeasure();
        MainContentGrid.InvalidateMeasure();
    }

    private double GetMaximumSftpPanelWidth(MainWindowViewModel vm)
    {
        var reservedRightWidth = vm.IsMonitorPanelVisible ? MonitorPanelWidth : 0;
        var maxWidth = MainContentGrid.Bounds.Width -
                       reservedRightWidth -
                       MinimumTerminalPanelWidth -
                       vm.SftpSplitterWidth.Value;
        return Math.Max(MinimumSftpPanelWidth, maxWidth);
    }

    private async void OnQuickSessionPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_quickSessionContext);
    }

    private void OnQuickSessionDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.RemoveQuickSessionCommand.Execute(_quickSessionContext);
        _quickSessionContext = null;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.CloseTab(_tabContext);
        _tabContext = null;
    }

    private async void OnTabPropertiesClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_tabContext.Session);
    }

    private void OnTabAddQuickClick(object? sender, RoutedEventArgs e)
    {
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

    private void ShowSessionManagerOnStartupIfNeeded()
    {
        if (Array.IndexOf(_startupArgs, "--rdp-smoke") >= 0 ||
            DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(vm.ShowSessionManagerOnStartupIfEnabled, DispatcherPriority.Background);
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
