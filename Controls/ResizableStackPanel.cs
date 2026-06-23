using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace ChiXueSsh.Controls;

public sealed class ResizableStackPanel : Panel
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<ResizableStackPanel, Orientation>(
            nameof(Orientation),
            Orientation.Horizontal);

    public static readonly StyledProperty<double> MinChildLengthProperty =
        AvaloniaProperty.Register<ResizableStackPanel, double>(
            nameof(MinChildLength),
            120);

    public static readonly StyledProperty<double> SplitterHitSizeProperty =
        AvaloniaProperty.Register<ResizableStackPanel, double>(
            nameof(SplitterHitSize),
            18);

    private readonly List<double> _weights = new();
    private readonly List<double> _starts = new();
    private readonly List<double> _lengths = new();
    private int _dragBoundaryIndex = -1;
    private double _dragStartPointer;
    private double _dragStartBeforeLength;
    private double _dragStartAfterLength;
    private bool _isDragging;
    private Cursor? _previousTopLevelCursor;
    private bool _hasTopLevelCursor;
    private TopLevel? _trackedTopLevel;
    private bool _topLevelHandlersAttached;

    public ResizableStackPanel()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerExitedEvent, OnPointerExited, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double MinChildLength
    {
        get => GetValue(MinChildLengthProperty);
        set => SetValue(MinChildLengthProperty, value);
    }

    public double SplitterHitSize
    {
        get => GetValue(SplitterHitSizeProperty);
        set => SetValue(SplitterHitSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureWeights();

        var count = Children.Count;
        if (count == 0)
            return default;

        var totalLength = GetPrimaryLength(availableSize);
        var crossLength = GetCrossLength(availableSize);
        if (double.IsInfinity(totalLength))
            totalLength = 0;
        if (double.IsInfinity(crossLength))
            crossLength = 0;

        for (var i = 0; i < count; i++)
        {
            var childLength = totalLength > 0 ? totalLength * _weights[i] / TotalWeight() : double.PositiveInfinity;
            Children[i].Measure(CreateSize(childLength, crossLength));
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureWeights();
        RebuildLayoutCache(finalSize);

        for (var i = 0; i < Children.Count; i++)
        {
            var rect = Orientation == Orientation.Horizontal
                ? new Rect(_starts[i], 0, _lengths[i], finalSize.Height)
                : new Rect(0, _starts[i], finalSize.Width, _lengths[i]);
            Children[i].Arrange(rect);
        }

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == OrientationProperty ||
            change.Property == MinChildLengthProperty ||
            change.Property == SplitterHitSizeProperty)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var boundary = HitTestBoundary(e.GetPosition(this));
        if (boundary < 0)
            return;

        StartDrag(boundary, GetPointerPrimaryPosition(e), e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            DragTo(GetPointerPrimaryPosition(e));
            ShowResizeCursor();
            e.Handled = true;
            return;
        }

        if (HitTestBoundary(e.GetPosition(this)) >= 0)
        {
            ShowResizeCursor();
            e.Handled = true;
        }
        else
        {
            ClearResizeCursor();
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;

        EndDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndDrag(null);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
            ClearResizeCursor();
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isDragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var boundary = HitTestBoundary(e.GetPosition(this));
        if (boundary < 0)
            return;

        StartDrag(boundary, GetPointerPrimaryPosition(e), e.Pointer);
        e.Handled = true;
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            DragTo(GetPointerPrimaryPosition(e));
            ShowResizeCursor();
            e.Handled = true;
            return;
        }

        if (HitTestBoundary(e.GetPosition(this)) >= 0)
        {
            ShowResizeCursor();
            e.Handled = true;
        }
        else
        {
            ClearResizeCursor();
        }
    }

    private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;

        EndDrag(e.Pointer);
        e.Handled = true;
    }

    private void StartDrag(int boundary, double pointerPosition, IPointer pointer)
    {
        EnsureLayoutCache();
        if (boundary < 0 || boundary + 1 >= _lengths.Count)
            return;

        _dragBoundaryIndex = boundary;
        _dragStartPointer = pointerPosition;
        _dragStartBeforeLength = _lengths[boundary];
        _dragStartAfterLength = _lengths[boundary + 1];
        _isDragging = true;
        pointer.Capture(this);
        ShowResizeCursor();
    }

    private void DragTo(double pointerPosition)
    {
        if (_dragBoundaryIndex < 0 || _dragBoundaryIndex + 1 >= Children.Count)
            return;

        var delta = pointerPosition - _dragStartPointer;
        var beforeLength = Math.Max(MinChildLength, _dragStartBeforeLength + delta);
        var afterLength = Math.Max(MinChildLength, _dragStartAfterLength - delta);
        var combined = _dragStartBeforeLength + _dragStartAfterLength;

        if (combined <= 0)
            return;

        if (beforeLength + afterLength > combined)
        {
            if (delta > 0)
                beforeLength = combined - afterLength;
            else
                afterLength = combined - beforeLength;
        }

        if (beforeLength < MinChildLength || afterLength < MinChildLength)
            return;

        var totalWeight = _weights[_dragBoundaryIndex] + _weights[_dragBoundaryIndex + 1];
        _weights[_dragBoundaryIndex] = totalWeight * beforeLength / combined;
        _weights[_dragBoundaryIndex + 1] = totalWeight * afterLength / combined;

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void EndDrag(IPointer? pointer)
    {
        _isDragging = false;
        _dragBoundaryIndex = -1;
        pointer?.Capture(null);
        ClearResizeCursor();
    }

    private int HitTestBoundary(Point position)
    {
        var primary = Orientation == Orientation.Horizontal ? position.X : position.Y;
        var cross = Orientation == Orientation.Horizontal ? position.Y : position.X;
        var primaryLength = GetPrimaryLength(Bounds.Size);
        var crossLength = GetCrossLength(Bounds.Size);
        if (primary < 0 || primary > primaryLength || cross < 0 || cross > crossLength)
            return -1;

        return HitTestBoundary(primary);
    }

    private int HitTestBoundary(double pointerPosition)
    {
        if (Children.Count < 2)
            return -1;

        EnsureLayoutCache();
        var halfHitSize = Math.Max(2, SplitterHitSize / 2);
        for (var i = 0; i < _starts.Count - 1; i++)
        {
            var boundary = _starts[i] + _lengths[i];
            if (Math.Abs(pointerPosition - boundary) <= halfHitSize)
                return i;
        }

        return -1;
    }

    private void EnsureLayoutCache()
    {
        if (_starts.Count == Children.Count && _lengths.Count == Children.Count)
            return;

        RebuildLayoutCache(Bounds.Size);
    }

    private void RebuildLayoutCache(Size size)
    {
        EnsureWeights();
        _starts.Clear();
        _lengths.Clear();

        var count = Children.Count;
        if (count == 0)
            return;

        var totalLength = Math.Max(0, GetPrimaryLength(size));
        var totalWeight = TotalWeight();
        var current = 0.0;

        for (var i = 0; i < count; i++)
        {
            var length = i == count - 1
                ? Math.Max(0, totalLength - current)
                : Math.Max(0, totalLength * _weights[i] / totalWeight);
            _starts.Add(current);
            _lengths.Add(length);
            current += length;
        }
    }

    private void EnsureWeights()
    {
        var count = Children.Count;
        if (count == _weights.Count)
            return;

        var oldWeights = _weights.ToArray();
        _weights.Clear();

        for (var i = 0; i < count; i++)
            _weights.Add(i < oldWeights.Length ? oldWeights[i] : 1);

        var total = TotalWeight();
        if (total <= 0)
        {
            for (var i = 0; i < count; i++)
                _weights[i] = 1;
        }

        _starts.Clear();
        _lengths.Clear();
    }

    private double TotalWeight()
    {
        var total = 0.0;
        foreach (var weight in _weights)
            total += Math.Max(0.0001, weight);
        return total;
    }

    private double GetPointerPrimaryPosition(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        return Orientation == Orientation.Horizontal ? position.X : position.Y;
    }

    private double GetPrimaryLength(Size size)
    {
        return Orientation == Orientation.Horizontal ? size.Width : size.Height;
    }

    private double GetCrossLength(Size size)
    {
        return Orientation == Orientation.Horizontal ? size.Height : size.Width;
    }

    private Size CreateSize(double primary, double cross)
    {
        return Orientation == Orientation.Horizontal
            ? new Size(primary, cross)
            : new Size(cross, primary);
    }

    private Cursor ResizeCursor()
    {
        return new Cursor(Orientation == Orientation.Horizontal
            ? StandardCursorType.SizeWestEast
            : StandardCursorType.SizeNorthSouth);
    }

    private void ShowResizeCursor()
    {
        var cursor = ResizeCursor();
        Cursor = cursor;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        AttachTopLevelHandlers(topLevel);

        if (!_hasTopLevelCursor)
        {
            _previousTopLevelCursor = topLevel.Cursor;
            _hasTopLevelCursor = true;
        }

        topLevel.Cursor = cursor;
    }

    private void ClearResizeCursor()
    {
        if (_isDragging)
            return;

        Cursor = null;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || !_hasTopLevelCursor)
            return;

        topLevel.Cursor = _previousTopLevelCursor;
        _previousTopLevelCursor = null;
        _hasTopLevelCursor = false;
        DetachTopLevelHandlers();
    }

    private void AttachTopLevelHandlers(TopLevel topLevel)
    {
        if (_topLevelHandlersAttached && _trackedTopLevel == topLevel)
            return;

        DetachTopLevelHandlers();
        _trackedTopLevel = topLevel;
        topLevel.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerMovedEvent, OnTopLevelPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _topLevelHandlersAttached = true;
    }

    private void DetachTopLevelHandlers()
    {
        if (!_topLevelHandlersAttached || _trackedTopLevel == null)
            return;

        _trackedTopLevel.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _trackedTopLevel.RemoveHandler(PointerMovedEvent, OnTopLevelPointerMoved);
        _trackedTopLevel.RemoveHandler(PointerReleasedEvent, OnTopLevelPointerReleased);
        _trackedTopLevel = null;
        _topLevelHandlersAttached = false;
    }
}
