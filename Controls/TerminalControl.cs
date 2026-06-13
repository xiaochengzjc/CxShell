using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.VisualTree;
using ChiXueSsh.Terminal;

namespace ChiXueSsh.Controls;

public class TerminalControl : Control
{
    private double _cellWidth;
    private double _cellHeight;
    private Typeface _typeface;
    private CellPosition? _selectionAnchor;
    private CellPosition? _selectionEnd;
    private bool _isSelecting;
    private int _scrollOffset;
    private TerminalBuffer? _observedBuffer;
    private int _lastScrollbackCount;
    private bool _isDraggingScrollbar;
    private double _scrollbarDragOffsetY;
    private bool _isPointerOverScrollbar;
    private readonly TerminalTextInputMethodClient _textInputMethodClient;
    private const double ScrollbarWidth = 16;
    private const double ScrollbarMinThumbHeight = 28;

    public static readonly StyledProperty<TerminalBuffer?> TerminalBufferProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalBuffer?>(nameof(TerminalBuffer));

    public TerminalBuffer? TerminalBuffer
    {
        get => GetValue(TerminalBufferProperty);
        set => SetValue(TerminalBufferProperty, value);
    }

    public event Action<string>? InputReceived;
    public event Action<int, int>? SizeChanged2;

    private int _columns = 80;
    private int _rows = 24;

    public int Columns => _columns;
    public int Rows => _rows;
    public bool HasSelection => _selectionAnchor.HasValue
        && _selectionEnd.HasValue
        && _selectionAnchor.Value != _selectionEnd.Value;

    public TerminalControl()
    {
        Focusable = true;
        InputMethod.SetIsInputMethodEnabled(this, true);
        TextInputOptions.SetContentType(this, TextInputContentType.Normal);
        TextInputOptions.SetMultiline(this, true);
        _textInputMethodClient = new TerminalTextInputMethodClient(this);
        TextInputMethodClientRequested += OnTextInputMethodClientRequested;
        _typeface = new Typeface("Cascadia Mono, Consolas, Courier New, monospace");
        CalculateCellSize();
    }

    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        e.Client = _textInputMethodClient;
    }

    private void CalculateCellSize()
    {
        var formattedText = new FormattedText(
            "M",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            14,
            Brushes.White);

        _cellWidth = formattedText.Width;
        _cellHeight = formattedText.Height;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TerminalBufferProperty)
        {
            if (_observedBuffer != null)
                _observedBuffer.Changed -= OnTerminalBufferChanged;

            _observedBuffer = TerminalBuffer;
            _lastScrollbackCount = _observedBuffer?.ScrollbackCount ?? 0;
            if (_observedBuffer != null)
                _observedBuffer.Changed += OnTerminalBufferChanged;

            _scrollOffset = 0;
            _selectionAnchor = null;
            _selectionEnd = null;
            TerminalBuffer?.Resize(_columns, _rows);
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? _columns * _cellWidth
            : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height)
            ? _rows * _cellHeight
            : availableSize.Height;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateTerminalSize(finalSize, notify: true);

        return finalSize;
    }

    public void SyncSizeToBounds()
    {
        UpdateTerminalSize(Bounds.Size, notify: true);
    }

    private void UpdateTerminalSize(Size size, bool notify)
    {
        if (_cellWidth <= 0
            || _cellHeight <= 0
            || double.IsNaN(size.Width)
            || double.IsNaN(size.Height)
            || double.IsInfinity(size.Width)
            || double.IsInfinity(size.Height)
            || size.Width <= 0
            || size.Height <= 0)
        {
            return;
        }

        var usableWidth = Math.Max(0, size.Width - ScrollbarWidth);
        int newCols = Math.Max(20, (int)Math.Floor(usableWidth / _cellWidth));
        int newRows = Math.Max(5, (int)Math.Floor(size.Height / _cellHeight));

        var changed = newCols != _columns || newRows != _rows;
        _columns = newCols;
        _rows = newRows;
        TerminalBuffer?.Resize(_columns, _rows);
        ClampScrollOffset();

        if (notify && changed)
            SizeChanged2?.Invoke(_columns, _rows);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var buffer = TerminalBuffer;
        if (buffer == null)
        {
            context.FillRectangle(
                new SolidColorBrush(TerminalColors.DefaultBackground),
                new Rect(Bounds.Size));
            return;
        }

        // Draw background
        context.FillRectangle(
            new SolidColorBrush(TerminalColors.DefaultBackground),
            new Rect(Bounds.Size));

        for (int row = 0; row < buffer.Rows; row++)
        {
            double y = row * _cellHeight;

            // Draw background segments
            int segStart = 0;
            var segColor = GetViewportCell(buffer, row, 0).Background;

            for (int col = 0; col <= buffer.Columns; col++)
            {
                var cellColor = col < buffer.Columns
                    ? GetViewportCell(buffer, row, col).Background
                    : TerminalColors.DefaultBackground;

                if (cellColor != segColor || col == buffer.Columns)
                {
                    if (segColor != TerminalColors.DefaultBackground)
                    {
                        context.FillRectangle(
                            new SolidColorBrush(segColor),
                            new Rect(segStart * _cellWidth, y, (col - segStart) * _cellWidth, _cellHeight));
                    }
                    segStart = col;
                    segColor = cellColor;
                }
            }

            DrawSelection(context, row, y);

            // Draw foreground text at cell positions so wide CJK characters keep cursor alignment.
            for (int col = 0; col < buffer.Columns; col++)
            {
                var cell = GetViewportCell(buffer, row, col);
                if (cell.IsWideContinuation || cell.Character == ' ')
                    continue;

                DrawTextRun(context, cell.Character.ToString(), col * _cellWidth, y, cell.Foreground, cell.Bold);
            }

            // Draw underline for cells that have it
            for (int col = 0; col < buffer.Columns; col++)
            {
                var cell = GetViewportCell(buffer, row, col);
                if (cell.Underline && cell.Character != ' ' && !cell.IsWideContinuation)
                {
                    var width = col + 1 < buffer.Columns && GetViewportCell(buffer, row, col + 1).IsWideContinuation
                        ? 2
                        : 1;
                    var pen = new Pen(new SolidColorBrush(cell.Foreground), 1);
                    context.DrawLine(pen,
                        new Point(col * _cellWidth, y + _cellHeight - 1),
                        new Point((col + width) * _cellWidth, y + _cellHeight - 1));
                }
            }
        }

        // Draw cursor
        if (buffer.CursorVisible && _scrollOffset == 0)
        {
            var cursorCol = buffer.CursorCol;
            var cursorCell = buffer.GetCell(buffer.CursorRow, cursorCol);
            var cursorWidth = !cursorCell.IsWideContinuation
                && cursorCol + 1 < buffer.Columns
                && buffer.GetCell(buffer.CursorRow, cursorCol + 1).IsWideContinuation
                ? _cellWidth * 2
                : _cellWidth;
            double cursorX = cursorCol * _cellWidth;
            double cursorY = buffer.CursorRow * _cellHeight;
            // Draw green cursor block (like Xshell style)
            context.FillRectangle(
                new SolidColorBrush(Color.Parse("#33FF33")),
                new Rect(cursorX, cursorY, cursorWidth, _cellHeight));
            // Re-draw the character under cursor in black (inverted) so it remains visible
            if (!cursorCell.IsWideContinuation && cursorCell.Character != '\0' && cursorCell.Character != ' ')
            {
                DrawTextRun(context, cursorCell.Character.ToString(),
                    cursorX, cursorY,
                    Colors.Black, cursorCell.Bold);
            }
        }

        DrawScrollbar(context, buffer);

        // Clear dirty rows
        buffer.DirtyRows.Clear();
    }

    private void DrawScrollbar(DrawingContext context, TerminalBuffer buffer)
    {
        if (!ShouldShowScrollbar(buffer))
            return;

        var track = GetScrollbarTrackRect();
        var thumb = GetScrollbarThumbRect(buffer, track);
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(90, 80, 86, 96)),
            track);
        context.FillRectangle(
            new SolidColorBrush(_isDraggingScrollbar
                ? Color.FromArgb(230, 150, 170, 205)
                : Color.FromArgb(190, 120, 140, 175)),
            thumb);
    }

    private void OnTerminalBufferChanged()
    {
        var count = TerminalBuffer?.ScrollbackCount ?? 0;
        var delta = count - _lastScrollbackCount;
        if (_scrollOffset > 0 && delta > 0)
        {
            _scrollOffset += delta;
            ClampScrollOffset();
        }

        _lastScrollbackCount = count;
    }

    private void DrawSelection(DrawingContext context, int row, double y)
    {
        if (!TryGetOrderedSelection(out var start, out var end))
            return;

        var buffer = TerminalBuffer;
        if (buffer == null || row < start.Row || row > end.Row)
            return;

        int startCol = row == start.Row ? start.Column : 0;
        int endColExclusive = row == end.Row ? end.Column : buffer.Columns;
        if (endColExclusive <= startCol)
            return;

        context.FillRectangle(
            new SolidColorBrush(Color.Parse("#8069A7FF")),
            new Rect(startCol * _cellWidth, y, (endColExclusive - startCol) * _cellWidth, _cellHeight));
    }

    private TerminalCell GetViewportCell(TerminalBuffer buffer, int row, int col)
    {
        return buffer.GetViewportCell(row, col, _scrollOffset);
    }

    private void DrawTextRun(DrawingContext context, string text, double x, double y,
        Color color, bool bold)
    {
        // Skip whitespace-only runs for performance
        bool allSpaces = true;
        foreach (var c in text)
        {
            if (c != ' ') { allSpaces = false; break; }
        }
        if (allSpaces) return;

        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            14,
            new SolidColorBrush(color));

        context.DrawText(ft, new Point(x, y));
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text != null)
        {
            ScrollToBottom();
            InputReceived?.Invoke(e.Text);
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var buffer = TerminalBuffer;
        var point = e.GetPosition(this);
        if (buffer != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsPointInScrollbar(point, buffer))
        {
            var track = GetScrollbarTrackRect();
            var thumb = GetScrollbarThumbRect(buffer, track);
            _isDraggingScrollbar = true;
            _scrollbarDragOffsetY = thumb.Contains(point) ? point.Y - thumb.Y : thumb.Height / 2;
            UpdateScrollOffsetFromScrollbar(point.Y, buffer);
            _selectionAnchor = null;
            _selectionEnd = null;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var position = GetCellPosition(e.GetPosition(this), includeCell: true);
        _selectionAnchor = position;
        _selectionEnd = position;
        _isSelecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdatePointerCursor(e.GetPosition(this));

        if (_isDraggingScrollbar)
        {
            var buffer = TerminalBuffer;
            if (buffer != null)
                UpdateScrollOffsetFromScrollbar(e.GetPosition(this).Y, buffer);

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_isSelecting || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _selectionEnd = GetCellPosition(e.GetPosition(this), includeCell: true);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOverScrollbar = false;
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDraggingScrollbar)
        {
            _isDraggingScrollbar = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_isSelecting || e.InitialPressMouseButton != MouseButton.Left)
            return;

        _selectionEnd = GetCellPosition(e.GetPosition(this), includeCell: true);
        _isSelecting = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var buffer = TerminalBuffer;
        if (buffer == null || buffer.ScrollbackCount == 0)
            return;

        var rows = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * 3));
        if (e.Delta.Y > 0)
            _scrollOffset += rows;
        else if (e.Delta.Y < 0)
            _scrollOffset -= rows;

        ClampScrollOffset();
        _selectionAnchor = null;
        _selectionEnd = null;
        InvalidateVisual();
        e.Handled = true;
    }

    public async Task CopySelectionAsync()
    {
        try
        {
            var text = GetSelectedText();
            if (string.IsNullOrEmpty(text) || !this.IsAttachedToVisualTree())
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error copying terminal selection: {ex.Message}");
        }
    }

    public async Task PasteAsync()
    {
        try
        {
            if (!this.IsAttachedToVisualTree())
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                ScrollToBottom();
                InputReceived?.Invoke(text.Replace("\r\n", "\r").Replace("\n", "\r"));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error pasting into terminal: {ex.Message}");
        }
    }

    private string GetSelectedText()
    {
        var buffer = TerminalBuffer;
        if (buffer == null || !TryGetOrderedSelection(out var start, out var end))
            return string.Empty;

        var result = new StringBuilder();
        for (int row = start.Row; row <= end.Row; row++)
        {
            int startCol = row == start.Row ? start.Column : 0;
            int endColExclusive = row == end.Row ? end.Column : buffer.Columns;
            var line = new StringBuilder();

            for (int col = startCol; col < endColExclusive; col++)
            {
                var cell = GetViewportCell(buffer, row, col);
                if (!cell.IsWideContinuation)
                    line.Append(cell.Character);
            }

            result.Append(line.ToString().TrimEnd());
            if (row < end.Row)
                result.AppendLine();
        }

        return result.ToString();
    }

    private CellPosition GetCellPosition(Point point, bool includeCell = false)
    {
        var buffer = TerminalBuffer;
        int columns = buffer?.Columns ?? _columns;
        int rows = buffer?.Rows ?? _rows;
        int row = Math.Clamp((int)(point.Y / _cellHeight), 0, rows - 1);
        int column = Math.Clamp((int)(point.X / _cellWidth), 0, columns - 1);

        if (includeCell && point.X >= column * _cellWidth + _cellWidth / 2)
            column++;

        return new CellPosition(row, column);
    }

    private bool TryGetOrderedSelection(out CellPosition start, out CellPosition end)
    {
        start = default;
        end = default;
        if (!HasSelection)
            return false;

        var anchor = _selectionAnchor!.Value;
        var selectionEnd = _selectionEnd!.Value;
        if (anchor.CompareTo(selectionEnd) <= 0)
        {
            start = anchor;
            end = selectionEnd;
        }
        else
        {
            start = selectionEnd;
            end = anchor;
        }

        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Ctrl+key combinations
        if (ctrl)
        {
            string? ctrlData = e.Key switch
            {
                Key.C => "\x03",  // ETX - interrupt
                Key.D => "\x04",  // EOT - EOF
                Key.Z => "\x1A",  // SUB - suspend
                Key.L => "\x0C",  // FF  - clear screen
                Key.A => "\x01",  // SOH - start of line
                Key.E => "\x05",  // ENQ - end of line
                Key.U => "\x15",  // NAK - clear line
                Key.K => "\x0B",  // VT  - kill to end
                Key.W => "\x17",  // ETB - delete word
                Key.R => "\x12",  // DC2 - reverse search
                _ => null
            };

            if (ctrlData != null)
            {
                ScrollToBottom();
                InputReceived?.Invoke(ctrlData);
                e.Handled = true;
                return;
            }
        }

        string? data = e.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\x7F",
            Key.Tab => "\t",
            Key.Escape => "\x1B",
            Key.Up => "\x1B[A",
            Key.Down => "\x1B[B",
            Key.Right => "\x1B[C",
            Key.Left => "\x1B[D",
            Key.Home => "\x1B[H",
            Key.End => "\x1B[F",
            Key.Delete => "\x1B[3~",
            Key.PageUp => "\x1B[5~",
            Key.PageDown => "\x1B[6~",
            _ => null
        };

        if (data != null)
        {
            ScrollToBottom();
            InputReceived?.Invoke(data);
            e.Handled = true;
        }
    }

    private void ScrollToBottom()
    {
        if (_scrollOffset == 0)
            return;

        _scrollOffset = 0;
        _selectionAnchor = null;
        _selectionEnd = null;
        InvalidateVisual();
    }

    private void ClampScrollOffset()
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, TerminalBuffer?.ScrollbackCount ?? 0);
    }

    private bool ShouldShowScrollbar(TerminalBuffer buffer)
    {
        return buffer.ScrollbackCount > 0 && Bounds.Height > ScrollbarMinThumbHeight;
    }

    private Rect GetScrollbarTrackRect()
    {
        return new Rect(Math.Max(0, Bounds.Width - ScrollbarWidth), 0, ScrollbarWidth, Bounds.Height);
    }

    private Rect GetScrollbarThumbRect(TerminalBuffer buffer, Rect track)
    {
        var totalRows = buffer.ScrollbackCount + buffer.Rows;
        var thumbHeight = Math.Clamp(
            track.Height * buffer.Rows / Math.Max(buffer.Rows, totalRows),
            ScrollbarMinThumbHeight,
            track.Height);
        var travel = Math.Max(0, track.Height - thumbHeight);
        var maxOffset = Math.Max(1, buffer.ScrollbackCount);
        var topRatio = (maxOffset - _scrollOffset) / (double)maxOffset;
        var thumbY = track.Y + travel * topRatio;
        return new Rect(track.X + 2, thumbY, Math.Max(4, track.Width - 4), thumbHeight);
    }

    private bool IsPointInScrollbar(Point point, TerminalBuffer buffer)
    {
        return ShouldShowScrollbar(buffer) && GetScrollbarTrackRect().Contains(point);
    }

    private void UpdatePointerCursor(Point point)
    {
        var buffer = TerminalBuffer;
        var overScrollbar = buffer != null && IsPointInScrollbar(point, buffer);
        if (overScrollbar == _isPointerOverScrollbar)
            return;

        _isPointerOverScrollbar = overScrollbar;
        Cursor = new Cursor(overScrollbar ? StandardCursorType.Arrow : StandardCursorType.Ibeam);
    }

    private void UpdateScrollOffsetFromScrollbar(double pointerY, TerminalBuffer buffer)
    {
        var track = GetScrollbarTrackRect();
        var thumb = GetScrollbarThumbRect(buffer, track);
        var travel = Math.Max(1, track.Height - thumb.Height);
        var thumbY = Math.Clamp(pointerY - _scrollbarDragOffsetY, track.Y, track.Bottom - thumb.Height);
        var topRatio = (thumbY - track.Y) / travel;
        _scrollOffset = (int)Math.Round(buffer.ScrollbackCount * (1 - topRatio));
        ClampScrollOffset();
    }

    private Rect GetCursorRectangle()
    {
        var buffer = TerminalBuffer;
        var col = buffer?.CursorCol ?? _columns;
        var row = buffer?.CursorRow ?? _rows;
        return new Rect(col * _cellWidth, row * _cellHeight, _cellWidth, _cellHeight);
    }

    private sealed class TerminalTextInputMethodClient : TextInputMethodClient
    {
        private readonly TerminalControl _owner;

        public TerminalTextInputMethodClient(TerminalControl owner)
        {
            _owner = owner;
        }

        public override Visual TextViewVisual => _owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => string.Empty;
        public override Rect CursorRectangle => _owner.GetCursorRectangle();
        public override TextSelection Selection
        {
            get => new(0, 0);
            set { }
        }

        public override void SetPreeditText(string? preeditText)
        {
        }

        public override void SetPreeditText(string? preeditText, int? cursorOffset)
        {
        }

        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
        }
    }

    private readonly record struct CellPosition(int Row, int Column) : IComparable<CellPosition>
    {
        public int CompareTo(CellPosition other)
        {
            int rowComparison = Row.CompareTo(other.Row);
            return rowComparison != 0 ? rowComparison : Column.CompareTo(other.Column);
        }
    }
}
