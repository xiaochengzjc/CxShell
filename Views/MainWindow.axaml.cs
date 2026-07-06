using System;
using System.Collections.Generic;
using Avalonia;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    private const double QuickSessionDragGhostOffsetX = -22;
    private const double QuickSessionDragGhostOffsetY = -7;
    private const double QuickSessionDropVerticalTolerance = 8;
    private const double TabDragThreshold = 6;
    private const double TabDropIndicatorWidth = 2;
    private const double TabDropIndicatorHeight = 24;
    private const double TabDropIndicatorVerticalOffset = 3;
    private const double TabDragGhostOffsetX = -22;
    private const double TabDragGhostOffsetY = -7;
    private const double TabDropVerticalTolerance = 24;
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

        if (TryHandleVncPasteShortcut(e))
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

    private bool TryHandleVncPasteShortcut(KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedTab.Vnc: { } vnc } ||
            e.Key != Key.V ||
            (e.KeyModifiers & KeyModifiers.Control) == 0 ||
            (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            return false;
        }

        e.Handled = true;
        _ = PasteLocalClipboardToVncAsync(vnc);
        return true;
    }

    private async Task PasteLocalClipboardToVncAsync(VncViewModel vnc)
    {
        try
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
                await vnc.SendClipboardTextAndPasteAsync(text);
        }
        catch
        {
            // Clipboard access can fail on some desktop backends.
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

        if (TryBeginTabHeaderDragFromPreview(source, e, vm))
            return;

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

    private bool TryBeginTabHeaderDragFromPreview(
        Avalonia.Controls.Control source,
        PointerPressedEventArgs e,
        MainWindowViewModel vm)
    {
        if (IsTabCloseButtonSource(source))
            return false;

        var tabItem = ResolveTabStripItem(source);
        var tab = ResolveTabFromItem(tabItem);
        var tabStrip = ResolveTabStrip(tabItem);
        if (tabItem == null ||
            tab == null ||
            tabStrip == null)
        {
            return false;
        }

        var anchor = ResolveTabHeaderControl(source) ?? FindTabHeaderControl(tab) ?? tabItem;
        BeginTabDrag(tab, anchor, tabStrip, e);
        vm.SelectTabCommand.Execute(tab);
        e.Handled = true;
        return true;
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
            ShowQuickSessionDragGhost(position);
        }

        ShowQuickSessionDragGhost(position);
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
        HideQuickSessionDragGhost();
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
        if (_quickSessionDragSession == null)
        {
            _quickSessionDropTargetSession = null;
            _quickSessionDropInsertAfter = false;
            HideQuickSessionDropIndicator();
            RefreshQuickSessionDragVisuals();
            return;
        }

        var hostPoint = this.TranslatePoint(point, QuickSessionDropIndicatorHost);
        if (hostPoint == null ||
            hostPoint.Value.Y < -QuickSessionDropVerticalTolerance ||
            hostPoint.Value.Y > QuickSessionDropIndicatorHost.Bounds.Height + QuickSessionDropVerticalTolerance)
        {
            _quickSessionDropTargetSession = null;
            _quickSessionDropInsertAfter = false;
            HideQuickSessionDropIndicator();
            RefreshQuickSessionDragVisuals();
            return;
        }

        var dropItems = GetQuickSessionDropItems();
        if (dropItems.Count <= 1)
        {
            _quickSessionDropTargetSession = null;
            _quickSessionDropInsertAfter = false;
            HideQuickSessionDropIndicator();
            RefreshQuickSessionDragVisuals();
            return;
        }

        var insertIndex = ResolveQuickSessionDropIndex(dropItems, hostPoint.Value.X);
        if (!TryResolveQuickSessionDropTarget(
                dropItems,
                insertIndex,
                _quickSessionDragSession.Id,
                out var targetSession,
                out var insertAfter))
        {
            _quickSessionDropTargetSession = null;
            _quickSessionDropInsertAfter = false;
            HideQuickSessionDropIndicator();
            RefreshQuickSessionDragVisuals();
            return;
        }

        var indicatorX = ResolveQuickSessionDropIndicatorX(dropItems, insertIndex);

        _quickSessionDropTargetSession = targetSession;
        _quickSessionDropInsertAfter = insertAfter;
        ShowQuickSessionDropIndicatorAt(indicatorX);
        RefreshQuickSessionDragVisuals();
    }

    private List<QuickSessionDropItem> GetQuickSessionDropItems()
    {
        var items = new List<QuickSessionDropItem>();
        foreach (var control in this.GetVisualDescendants().OfType<Avalonia.Controls.Control>())
        {
            if (!control.Classes.Contains(QuickSessionButtonClass) ||
                control.DataContext is not SessionInfo session ||
                control.Bounds.Width <= 0)
            {
                continue;
            }

            var leftPoint = control.TranslatePoint(new Point(0, 0), QuickSessionDropIndicatorHost);
            if (leftPoint == null)
                continue;

            items.Add(new QuickSessionDropItem(
                session,
                leftPoint.Value.X,
                leftPoint.Value.X + control.Bounds.Width));
        }

        items.Sort(static (left, right) => left.Left.CompareTo(right.Left));
        return items;
    }

    private static int ResolveQuickSessionDropIndex(IReadOnlyList<QuickSessionDropItem> items, double pointerX)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var itemCenter = items[i].Left + (items[i].Right - items[i].Left) / 2;
            if (pointerX < itemCenter)
                return i;
        }

        return items.Count;
    }

    private static bool TryResolveQuickSessionDropTarget(
        IReadOnlyList<QuickSessionDropItem> items,
        int insertIndex,
        Guid draggingSessionId,
        out SessionInfo? targetSession,
        out bool insertAfter)
    {
        targetSession = null;
        insertAfter = false;

        var dragIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Session.Id == draggingSessionId)
            {
                dragIndex = i;
                break;
            }
        }

        if (dragIndex < 0 ||
            insertIndex == dragIndex ||
            insertIndex == dragIndex + 1)
        {
            return false;
        }

        if (insertIndex <= 0)
        {
            targetSession = items[0].Session;
            insertAfter = false;
        }
        else if (insertIndex >= items.Count)
        {
            targetSession = items[^1].Session;
            insertAfter = true;
        }
        else
        {
            targetSession = items[insertIndex].Session;
            insertAfter = false;
        }

        return targetSession.Id != draggingSessionId;
    }

    private static double ResolveQuickSessionDropIndicatorX(
        IReadOnlyList<QuickSessionDropItem> items,
        int insertIndex)
    {
        if (insertIndex <= 0)
            return items[0].Left;

        if (insertIndex >= items.Count)
            return items[^1].Right;

        return (items[insertIndex - 1].Right + items[insertIndex].Left) / 2;
    }

    private void ShowQuickSessionDropIndicatorAt(double indicatorX)
    {
        var left = Math.Round(indicatorX - QuickSessionDropIndicatorWidth / 2);
        QuickSessionDropIndicator.Margin = new Thickness(
            left,
            0,
            0,
            0);
        QuickSessionDropIndicator.IsVisible = true;
    }

    private void ShowQuickSessionDragGhost(Point pointerPosition)
    {
        var overlayPoint = this.TranslatePoint(pointerPosition, TabDropOverlay);
        if (overlayPoint == null)
        {
            HideQuickSessionDragGhost();
            return;
        }

        Avalonia.Controls.Canvas.SetLeft(
            QuickSessionDragGhost,
            Math.Round(overlayPoint.Value.X + QuickSessionDragGhostOffsetX));
        Avalonia.Controls.Canvas.SetTop(
            QuickSessionDragGhost,
            Math.Round(overlayPoint.Value.Y + QuickSessionDragGhostOffsetY));
        QuickSessionDragGhost.IsVisible = true;
    }

    private void HideQuickSessionDragGhost()
    {
        QuickSessionDragGhost.IsVisible = false;
    }

    private readonly record struct QuickSessionDropItem(
        SessionInfo Session,
        double Left,
        double Right);

    private void RefreshQuickSessionDragVisuals()
    {
        var dragControl = _quickSessionDragSession?.Id is { } dragId
            ? FindQuickSessionControlById(dragId) ?? _quickSessionDragControl
            : null;
        SetQuickSessionClassControl(ref _quickSessionDragControl, dragControl, QuickSessionDraggingClass);
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
            BeginTabDrag(tab, anchor, e);
            vm.SelectTabCommand.Execute(tab);
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
        BeginTabDrag(tab, anchor, ResolveTabStrip(anchor), e);
    }

    private void BeginTabDrag(
        TerminalTabViewModel tab,
        Avalonia.Controls.Control anchor,
        TabStrip? tabStrip,
        PointerPressedEventArgs e)
    {
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
            ShowTabDragGhost(position);
        }

        ShowTabDragGhost(position);
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
        HideTabDragGhost();
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
        if (_tabDragTab == null ||
            _tabDragStrip == null)
        {
            _tabDropTargetTab = null;
            _tabDropInsertAfter = false;
            HideTabDropIndicator();
            RefreshTabDragVisuals();
            return;
        }

        var stripPoint = this.TranslatePoint(point, _tabDragStrip);
        if (stripPoint == null ||
            stripPoint.Value.Y < -TabDropVerticalTolerance ||
            stripPoint.Value.Y > _tabDragStrip.Bounds.Height + TabDropVerticalTolerance)
        {
            _tabDropTargetTab = null;
            _tabDropInsertAfter = false;
            HideTabDropIndicator();
            RefreshTabDragVisuals();
            return;
        }

        var dropItems = GetTabDropItems(_tabDragStrip, _tabDragTab);
        if (dropItems.Count == 0)
        {
            _tabDropTargetTab = null;
            _tabDropInsertAfter = false;
            HideTabDropIndicator();
            RefreshTabDragVisuals();
            return;
        }

        var overlayPoint = this.TranslatePoint(point, TabDropOverlay);
        if (overlayPoint == null)
        {
            _tabDropTargetTab = null;
            _tabDropInsertAfter = false;
            HideTabDropIndicator();
            RefreshTabDragVisuals();
            return;
        }

        var insertIndex = ResolveTabDropIndex(dropItems, overlayPoint.Value.X);
        var insertAfter = insertIndex >= dropItems.Count;
        var targetItem = insertAfter
            ? dropItems[^1]
            : dropItems[insertIndex];
        var indicatorX = ResolveTabDropIndicatorX(dropItems, insertIndex);
        var indicatorY = ResolveTabDropIndicatorY(dropItems);

        _tabDropTargetTab = targetItem.Tab;
        _tabDropInsertAfter = insertAfter;
        ShowTabDropIndicatorAt(indicatorX, indicatorY);
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

    private List<TabDropItem> GetTabDropItems(TabStrip tabStrip, TerminalTabViewModel draggingTab)
    {
        var items = new List<TabDropItem>();
        foreach (var item in this.GetVisualDescendants().OfType<TabStripItem>())
        {
            var tab = ResolveTabFromItem(item);
            if (tab == null ||
                tab == draggingTab ||
                ResolveTabStrip(item) != tabStrip ||
                item.Bounds.Width <= 0)
            {
                continue;
            }

            var topLeft = item.TranslatePoint(new Point(0, 0), TabDropOverlay);
            if (topLeft == null)
                continue;

            items.Add(new TabDropItem(
                tab,
                topLeft.Value.X,
                topLeft.Value.X + item.Bounds.Width,
                topLeft.Value.Y,
                topLeft.Value.Y + item.Bounds.Height));
        }

        items.Sort(static (left, right) => left.Left.CompareTo(right.Left));
        return items;
    }

    private static int ResolveTabDropIndex(IReadOnlyList<TabDropItem> items, double pointerX)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var itemCenter = items[i].Left + (items[i].Right - items[i].Left) / 2;
            if (pointerX < itemCenter)
                return i;
        }

        return items.Count;
    }

    private static double ResolveTabDropIndicatorX(IReadOnlyList<TabDropItem> items, int insertIndex)
    {
        if (insertIndex <= 0)
            return items[0].Left;

        if (insertIndex >= items.Count)
            return items[^1].Right;

        return (items[insertIndex - 1].Right + items[insertIndex].Left) / 2;
    }

    private static double ResolveTabDropIndicatorY(IReadOnlyList<TabDropItem> items)
    {
        var top = items.Min(static item => item.Top);
        var bottom = items.Max(static item => item.Bottom);
        return top + (bottom - top) / 2;
    }

    private void ShowTabDropIndicatorAt(double indicatorX, double indicatorY)
    {
        Avalonia.Controls.Canvas.SetLeft(
            TabDropIndicator,
            Math.Round(indicatorX - TabDropIndicatorWidth / 2));
        Avalonia.Controls.Canvas.SetTop(
            TabDropIndicator,
            Math.Round(indicatorY - TabDropIndicatorHeight / 2 + TabDropIndicatorVerticalOffset));
        TabDropIndicator.IsVisible = true;
    }

    private void ShowTabDragGhost(Point pointerPosition)
    {
        var overlayPoint = this.TranslatePoint(pointerPosition, TabDropOverlay);
        if (overlayPoint == null)
        {
            HideTabDragGhost();
            return;
        }

        Avalonia.Controls.Canvas.SetLeft(
            TabDragGhost,
            Math.Round(overlayPoint.Value.X + TabDragGhostOffsetX));
        Avalonia.Controls.Canvas.SetTop(
            TabDragGhost,
            Math.Round(overlayPoint.Value.Y + TabDragGhostOffsetY));
        TabDragGhost.IsVisible = true;
    }

    private void HideTabDragGhost()
    {
        TabDragGhost.IsVisible = false;
    }

    private readonly record struct TabDropItem(
        TerminalTabViewModel Tab,
        double Left,
        double Right,
        double Top,
        double Bottom);

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

    private static Avalonia.Controls.Control? ResolveTabHeaderControl(Avalonia.Controls.Control? source)
    {
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current.Classes.Contains(SessionTabHeaderClass))
                return current;
        }

        return null;
    }

    private static bool IsTabCloseButtonSource(Avalonia.Controls.Control source)
    {
        foreach (var current in EnumerateControlLineage(source))
        {
            if (current.Name == "PART_ItemCloseButton")
                return true;
        }

        return false;
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

    private async void OnSendRemoteClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedTab.Vnc: { } vnc })
            return;

        var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            await vnc.SendClipboardTextAndPasteAsync(text);
    }

    private async void OnSendRemoteCtrlAltDelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { SelectedTab.Vnc: { } vnc })
            return;

        await vnc.SendCtrlAltDeleteAsync();
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
