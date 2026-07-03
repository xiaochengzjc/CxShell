using System;
using System.Collections.Generic;
using Avalonia;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private SessionInfo? _quickSessionDragSession;
    private Avalonia.Controls.Control? _quickSessionDragControl;
    private Point _quickSessionDragStart;
    private bool _isQuickSessionDragging;
    private bool _quickSessionDragMoved;
    private SessionInfo? _quickSessionDropTargetSession;
    private bool _quickSessionDropInsertAfter;
    private TerminalTabViewModel? _tabContext;
    private TerminalTabViewModel? _tabDragTab;
    private TerminalTabViewModel? _tabDropTargetTab;
    private Avalonia.Controls.Control? _tabDragControl;
    private Avalonia.Controls.Control? _tabDragCaptureControl;
    private TabStrip? _tabDragStrip;
    private Point _tabDragStart;
    private bool _isTabDragging;
    private bool _tabDragMoved;
    private bool _tabDropInsertAfter;
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
    private const double QuickSessionDragThreshold = 6;
    private const double QuickSessionDropIndicatorWidth = 2;
    private const double TabDragThreshold = 6;
    private const double TabDropIndicatorWidth = 2;
    private const double TabDropIndicatorHeight = 24;
    private const string QuickSessionButtonClass = "quick-session-bar-button";
    private const string QuickSessionDraggingClass = "quick-session-dragging";
    private const string QuickSessionDragActiveClass = "quick-session-drag-active";
    private const string SessionTabHeaderClass = "session-tab-header";
    private const string SessionTabDraggingClass = "tab-dragging";

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
        AddHandler(PointerMovedEvent, OnQuickSessionDragPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnQuickSessionDragPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnQuickSessionDragPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTabDragPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTabDragPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnTabDragPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (DataContext is MainWindowViewModel vm)
            vm.StartAutomaticUpdateCheck(_startupArgs);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandleQuickCommandShortcut(e))
            return;

        if (e.Key != Key.Escape)
            return;

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
        {
            vm.ExitTerminalFullScreen();
            e.Handled = true;
        }
    }

    private bool TryHandleQuickCommandShortcut(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            (e.KeyModifiers & KeyModifiers.Control) == 0 ||
            (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            return false;
        }

        var index = e.Key >= Key.D1 && e.Key <= Key.D9
            ? (int)e.Key - (int)Key.D1
            : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9
                ? (int)e.Key - (int)Key.NumPad1
                : -1;
        if (index < 0 || !vm.ExecuteQuickCommandByIndex(index))
            return false;

        e.Handled = true;
        return true;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            e.Source is not Avalonia.Controls.Control source)
        {
            return;
        }

        foreach (var current in EnumerateControlLineage(source))
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
        }
    }

    private void OnQuickSessionTagPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control { DataContext: SessionInfo session } anchor)
            return;

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ResetQuickSessionDrag();
            _quickSessionContext = session;
            ShowQuickSessionContextMenu(anchor);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed && properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        _quickSessionDragSession = session;
        _quickSessionDragControl = anchor;
        _quickSessionDropTargetSession = null;
        _quickSessionDragStart = e.GetPosition(this);
        _isQuickSessionDragging = false;
        _quickSessionDragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnQuickSessionDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_quickSessionDragSession == null)
            return;

        var position = e.GetPosition(this);
        if (!_isQuickSessionDragging)
        {
            var delta = position - _quickSessionDragStart;
            if (Math.Abs(delta.X) < QuickSessionDragThreshold &&
                Math.Abs(delta.Y) < QuickSessionDragThreshold)
            {
                return;
            }

            _isQuickSessionDragging = true;
            _quickSessionDragMoved = true;
            SetQuickSessionDragActiveVisual(true);
            RefreshQuickSessionDragVisuals();
        }

        UpdateQuickSessionDropTarget(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnQuickSessionDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_quickSessionDragSession == null)
            return;

        var dragSession = _quickSessionDragSession;
        var wasDragging = _isQuickSessionDragging || _quickSessionDragMoved;
        var releaseTarget = ResolveQuickSessionContextAt(e.GetPosition(this));
        if (wasDragging)
            UpdateQuickSessionDropTarget(e.GetPosition(this));

        var dropTarget = _quickSessionDropTargetSession;
        var insertAfter = _quickSessionDropInsertAfter;
        e.Pointer.Capture(null);
        ResetQuickSessionDrag();

        if (!wasDragging &&
            releaseTarget?.Id == dragSession.Id &&
            DataContext is MainWindowViewModel vm)
        {
            vm.ConnectQuickSessionCommand.Execute(dragSession);
            e.Handled = true;
            return;
        }

        if (wasDragging &&
            dropTarget != null &&
            dropTarget.Id != dragSession.Id &&
            DataContext is MainWindowViewModel moveVm)
        {
            moveVm.MoveQuickSession(dragSession, dropTarget, insertAfter);
        }

        if (wasDragging)
            e.Handled = true;
    }

    private void OnQuickSessionDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_quickSessionDragSession != null)
            ResetQuickSessionDrag();
    }

    private void ResetQuickSessionDrag()
    {
        SetQuickSessionClassControl(ref _quickSessionDragControl, null, QuickSessionDraggingClass);
        SetQuickSessionDragActiveVisual(false);
        HideQuickSessionDropIndicator();
        _quickSessionDragSession = null;
        _isQuickSessionDragging = false;
        _quickSessionDragMoved = false;
        _quickSessionDropTargetSession = null;
        _quickSessionDropInsertAfter = false;
        _quickSessionDragStart = default;
    }

    private void UpdateQuickSessionDropTarget(Point point)
    {
        var targetControl = ResolveQuickSessionControlAt(point);
        var target = targetControl?.DataContext as SessionInfo;
        if (targetControl == null ||
            target == null ||
            _quickSessionDragSession == null ||
            target.Id == _quickSessionDragSession.Id)
        {
            _quickSessionDropTargetSession = null;
            _quickSessionDropInsertAfter = false;
            HideQuickSessionDropIndicator();
            RefreshQuickSessionDragVisuals();
            return;
        }

        var targetPoint = this.TranslatePoint(point, targetControl);
        _quickSessionDropTargetSession = target;
        _quickSessionDropInsertAfter = targetPoint?.X > targetControl.Bounds.Width / 2;
        ShowQuickSessionDropIndicator(targetControl, _quickSessionDropInsertAfter);
        RefreshQuickSessionDragVisuals();
    }

    private void RefreshQuickSessionDragVisuals()
    {
        var dragControl = _quickSessionDragSession?.Id is { } dragId
            ? FindQuickSessionControlById(dragId) ?? _quickSessionDragControl
            : null;
        SetQuickSessionClassControl(ref _quickSessionDragControl, dragControl, QuickSessionDraggingClass);
    }

    private void ShowQuickSessionDropIndicator(Avalonia.Controls.Control targetControl, bool insertAfter)
    {
        var edgeX = insertAfter ? targetControl.Bounds.Width : 0;
        var indicatorPoint = targetControl.TranslatePoint(
            new Point(edgeX, targetControl.Bounds.Height / 2),
            QuickSessionDropIndicatorHost);
        if (indicatorPoint == null)
        {
            HideQuickSessionDropIndicator();
            return;
        }

        QuickSessionDropIndicator.Margin = new Thickness(
            indicatorPoint.Value.X - QuickSessionDropIndicatorWidth / 2,
            0,
            0,
            0);
        QuickSessionDropIndicator.IsVisible = true;
    }

    private void HideQuickSessionDropIndicator()
    {
        QuickSessionDropIndicator.IsVisible = false;
    }

    private void SetQuickSessionDragActiveVisual(bool isActive)
    {
        if (isActive)
        {
            if (!QuickSessionDropIndicatorHost.Classes.Contains(QuickSessionDragActiveClass))
                QuickSessionDropIndicatorHost.Classes.Add(QuickSessionDragActiveClass);
        }
        else
        {
            QuickSessionDropIndicatorHost.Classes.Remove(QuickSessionDragActiveClass);
        }
    }

    private Avalonia.Controls.Control? FindQuickSessionControlById(Guid sessionId)
    {
        return this.GetVisualDescendants()
            .OfType<Avalonia.Controls.Control>()
            .FirstOrDefault(control =>
                control.Classes.Contains(QuickSessionButtonClass) &&
                control.DataContext is SessionInfo session &&
                session.Id == sessionId);
    }

    private static void SetQuickSessionClassControl(
        ref Avalonia.Controls.Control? current,
        Avalonia.Controls.Control? next,
        string className)
    {
        if (!ReferenceEquals(current, next))
        {
            SetQuickSessionVisualClass(current, className, false);
            current = next;
        }

        SetQuickSessionVisualClass(current, className, current != null);
    }

    private static void SetQuickSessionVisualClass(
        Avalonia.Controls.Control? control,
        string className,
        bool isEnabled)
    {
        if (control == null)
            return;

        if (isEnabled)
        {
            if (!control.Classes.Contains(className))
                control.Classes.Add(className);
        }
        else
        {
            control.Classes.Remove(className);
        }
    }

    private static SessionInfo? ResolveQuickSessionContext(Avalonia.Controls.Control? source)
    {
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current.DataContext is SessionInfo session)
                return session;
        }

        return null;
    }

    private SessionInfo? ResolveQuickSessionContextAt(Point point)
    {
        return ResolveQuickSessionControlAt(point)?.DataContext as SessionInfo;
    }

    private Avalonia.Controls.Control? ResolveQuickSessionControlAt(Point point)
    {
        return this.GetVisualsAt(point)
            .OfType<Avalonia.Controls.Control>()
            .Select(ResolveQuickSessionControl)
            .FirstOrDefault(control => control != null);
    }

    private static Avalonia.Controls.Control? ResolveQuickSessionControl(Avalonia.Controls.Control? source)
    {
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current.Classes.Contains(QuickSessionButtonClass) &&
                current.DataContext is SessionInfo)
            {
                return current;
            }
        }

        return null;
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
            BeginTabDrag(tab, anchor, e);
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ResetTabDrag();
            _tabContext = tab;
            vm.SelectTabCommand.Execute(tab);
            ShowTabContextMenu(anchor, vm.AddCurrentSessionToQuickBarCommand.CanExecute(null));
            e.Handled = true;
        }
    }

    private void BeginTabDrag(TerminalTabViewModel tab, Avalonia.Controls.Control anchor, PointerPressedEventArgs e)
    {
        var tabStrip = ResolveTabStrip(anchor);
        if (tabStrip == null)
            return;

        _tabDragTab = tab;
        _tabDragControl = anchor;
        _tabDragCaptureControl = this;
        _tabDragStrip = tabStrip;
        _tabDropTargetTab = null;
        _tabDropInsertAfter = false;
        _tabDragStart = e.GetPosition(this);
        _isTabDragging = false;
        _tabDragMoved = false;
        e.Pointer.Capture(this);
    }

    private void OnTabDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragTab == null)
            return;

        var position = e.GetPosition(this);
        if (!_isTabDragging)
        {
            var delta = position - _tabDragStart;
            if (Math.Abs(delta.X) < TabDragThreshold &&
                Math.Abs(delta.Y) < TabDragThreshold)
            {
                return;
            }

            _isTabDragging = true;
            _tabDragMoved = true;
            RefreshTabDragVisuals();
        }

        UpdateTabDropTarget(position);
        e.Handled = true;
    }

    private void OnTabDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_tabDragTab == null)
            return;

        var dragTab = _tabDragTab;
        var wasDragging = _isTabDragging || _tabDragMoved;
        if (wasDragging)
            UpdateTabDropTarget(e.GetPosition(this));

        var dropTarget = _tabDropTargetTab;
        var insertAfter = _tabDropInsertAfter;
        e.Pointer.Capture(null);
        ResetTabDrag();

        if (wasDragging &&
            dropTarget != null &&
            dropTarget != dragTab &&
            DataContext is MainWindowViewModel vm)
        {
            vm.MoveTabWithinSameStrip(dragTab, dropTarget, insertAfter);
            e.Handled = true;
        }
        else if (wasDragging)
        {
            e.Handled = true;
        }
    }

    private void OnTabDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_tabDragTab != null &&
            _tabDragCaptureControl != null &&
            ReferenceEquals(e.Source, _tabDragCaptureControl))
        {
            ResetTabDrag();
        }
    }

    private void ResetTabDrag()
    {
        SetQuickSessionVisualClass(_tabDragControl, SessionTabDraggingClass, false);
        HideTabDropIndicator();
        _tabDragTab = null;
        _tabDropTargetTab = null;
        _tabDragControl = null;
        _tabDragCaptureControl = null;
        _tabDragStrip = null;
        _tabDragStart = default;
        _isTabDragging = false;
        _tabDragMoved = false;
        _tabDropInsertAfter = false;
    }

    private void UpdateTabDropTarget(Point point)
    {
        var targetItem = ResolveTabStripItemAt(point);
        var target = ResolveTabFromItem(targetItem);
        var targetStrip = ResolveTabStrip(targetItem);
        if (targetItem == null ||
            target == null ||
            targetStrip == null ||
            _tabDragTab == null ||
            _tabDragStrip == null ||
            target == _tabDragTab ||
            targetStrip != _tabDragStrip)
        {
            _tabDropTargetTab = null;
            _tabDropInsertAfter = false;
            HideTabDropIndicator();
            RefreshTabDragVisuals();
            return;
        }

        var targetPoint = this.TranslatePoint(point, targetItem);
        _tabDropTargetTab = target;
        _tabDropInsertAfter = targetPoint?.X > targetItem.Bounds.Width / 2;
        ShowTabDropIndicator(targetItem, _tabDropInsertAfter);
        RefreshTabDragVisuals();
    }

    private void RefreshTabDragVisuals()
    {
        var dragControl = _tabDragTab != null
            ? FindTabHeaderControl(_tabDragTab) ?? _tabDragControl
            : null;

        if (!ReferenceEquals(_tabDragControl, dragControl))
        {
            SetQuickSessionVisualClass(_tabDragControl, SessionTabDraggingClass, false);
            _tabDragControl = dragControl;
        }

        SetQuickSessionVisualClass(_tabDragControl, SessionTabDraggingClass, _tabDragControl != null);
    }

    private void ShowTabDropIndicator(Avalonia.Controls.Control targetControl, bool insertAfter)
    {
        var edgeX = insertAfter ? targetControl.Bounds.Width : 0;
        var indicatorPoint = targetControl.TranslatePoint(
            new Point(edgeX, targetControl.Bounds.Height / 2),
            TabDropOverlay);
        if (indicatorPoint == null)
        {
            HideTabDropIndicator();
            return;
        }

        Avalonia.Controls.Canvas.SetLeft(TabDropIndicator, indicatorPoint.Value.X - TabDropIndicatorWidth / 2);
        Avalonia.Controls.Canvas.SetTop(TabDropIndicator, indicatorPoint.Value.Y - TabDropIndicatorHeight / 2);
        TabDropIndicator.IsVisible = true;
    }

    private void HideTabDropIndicator()
    {
        TabDropIndicator.IsVisible = false;
    }

    private Avalonia.Controls.Control? FindTabHeaderControl(TerminalTabViewModel tab)
    {
        return this.GetVisualDescendants()
            .OfType<Avalonia.Controls.Control>()
            .FirstOrDefault(control =>
                control.Classes.Contains(SessionTabHeaderClass) &&
                ReferenceEquals(control.DataContext, tab));
    }

    private TabStripItem? ResolveTabStripItemAt(Point point)
    {
        return this.GetVisualsAt(point)
            .OfType<Avalonia.Controls.Control>()
            .Select(ResolveTabStripItem)
            .FirstOrDefault(item => item != null);
    }

    private static TerminalTabViewModel? ResolveTabFromItem(TabStripItem? item)
    {
        return item?.DataContext as TerminalTabViewModel ??
               item?.Content as TerminalTabViewModel;
    }

    private static TabStripItem? ResolveTabStripItem(Avalonia.Controls.Control? source)
    {
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current is TabStripItem item)
                return item;
        }

        return null;
    }

    private static TabStrip? ResolveTabStrip(Avalonia.Controls.Control? source)
    {
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current is TabStrip tabStrip)
                return tabStrip;
        }

        return null;
    }

    private static IEnumerable<Avalonia.Controls.Control> EnumerateControlLineage(Avalonia.Controls.Control? source)
    {
        var current = source;
        var seen = new HashSet<Avalonia.Controls.Control>(ReferenceEqualityComparer.Instance);
        while (current != null && seen.Add(current))
        {
            yield return current;
            current = GetParentControl(current);
        }
    }

    private static Avalonia.Controls.Control? GetParentControl(Avalonia.Controls.Control control)
    {
        var visualParent = control.GetVisualParent() as Avalonia.Controls.Control;
        if (visualParent != null && !ReferenceEquals(visualParent, control))
            return visualParent;

        var logicalParent = control.Parent as Avalonia.Controls.Control;
        if (logicalParent != null && !ReferenceEquals(logicalParent, control))
            return logicalParent;

        return null;
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
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current.DataContext is TerminalTabViewModel tab)
            {
                anchor = current;
                return tab;
            }
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
        AddMenuItem(menu, vm.TabDuplicateText, () => OnTabDuplicateClick(anchor, new RoutedEventArgs()));
        menu.Items.Add(new AtomMenuSeparator());
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

    private void OnHelpButtonClick(object? sender, RoutedEventArgs e)
    {
        HelpPopup.PlacementTarget = HelpButton;
        HelpPopup.IsOpen = true;
    }

    private void OnHelpMenuItemClick(object? sender, RoutedEventArgs e)
    {
        HelpPopup.Close();
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

    private async void OnTabDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (_tabContext == null || DataContext is not MainWindowViewModel vm)
            return;

        var tab = _tabContext;
        _tabContext = null;
        await vm.DuplicateTab(tab);
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
