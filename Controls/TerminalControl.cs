using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ChiXueSsh.Terminal;

namespace ChiXueSsh.Controls;

public class TerminalControl : Control
{
    private double _cellWidth;
    private double _cellHeight;
    private Typeface _typeface;

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

    public TerminalControl()
    {
        Focusable = true;
        _typeface = new Typeface("Cascadia Mono, Consolas, Courier New, monospace");
        CalculateCellSize();
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
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            Math.Min(availableSize.Width, _columns * _cellWidth + 16),
            Math.Min(availableSize.Height, _rows * _cellHeight + 8));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int newCols = Math.Max(20, (int)(finalSize.Width / _cellWidth));
        int newRows = Math.Max(5, (int)(finalSize.Height / _cellHeight));

        if (newCols != _columns || newRows != _rows)
        {
            _columns = newCols;
            _rows = newRows;
            TerminalBuffer?.Resize(_columns, _rows);
            SizeChanged2?.Invoke(_columns, _rows);
        }

        return finalSize;
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
            var segColor = buffer.GetCell(row, 0).Background;

            for (int col = 0; col <= buffer.Columns; col++)
            {
                var cellColor = col < buffer.Columns
                    ? buffer.GetCell(row, col).Background
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

            // Draw foreground text
            int textStart = 0;
            var textBuilder = new System.Text.StringBuilder();
            var fgColor = buffer.GetCell(row, 0).Foreground;
            bool isBold = buffer.GetCell(row, 0).Bold;

            for (int col = 0; col <= buffer.Columns; col++)
            {
                var cell = col < buffer.Columns ? buffer.GetCell(row, col) : default;
                bool sameStyle = col < buffer.Columns
                    && cell.Foreground == fgColor
                    && cell.Bold == isBold
                    && cell.Character != ' ' || col < buffer.Columns && cell.Character == ' ' && textBuilder.Length == 0;

                // Simplified: just flush when color changes or at end
                if (col == buffer.Columns || (col > 0 && cell.Foreground != fgColor))
                {
                    if (textBuilder.Length > 0)
                    {
                        DrawTextRun(context, textBuilder.ToString(), textStart * _cellWidth, y, fgColor, isBold);
                        textBuilder.Clear();
                    }
                    if (col < buffer.Columns)
                    {
                        textStart = col;
                        fgColor = cell.Foreground;
                        isBold = cell.Bold;
                        textBuilder.Append(cell.Character);
                    }
                }
                else if (col < buffer.Columns)
                {
                    if (textBuilder.Length == 0)
                    {
                        textStart = col;
                        fgColor = cell.Foreground;
                        isBold = cell.Bold;
                    }
                    textBuilder.Append(cell.Character);
                }
            }

            // Draw underline for cells that have it
            for (int col = 0; col < buffer.Columns; col++)
            {
                var cell = buffer.GetCell(row, col);
                if (cell.Underline && cell.Character != ' ')
                {
                    var pen = new Pen(new SolidColorBrush(cell.Foreground), 1);
                    context.DrawLine(pen,
                        new Point(col * _cellWidth, y + _cellHeight - 1),
                        new Point((col + 1) * _cellWidth, y + _cellHeight - 1));
                }
            }
        }

        // Draw cursor
        if (buffer.CursorVisible)
        {
            double cursorX = buffer.CursorCol * _cellWidth;
            double cursorY = buffer.CursorRow * _cellHeight;
            // Draw green cursor block (like Xshell style)
            context.FillRectangle(
                new SolidColorBrush(Color.Parse("#33FF33")),
                new Rect(cursorX, cursorY, _cellWidth, _cellHeight));
            // Re-draw the character under cursor in black (inverted) so it remains visible
            var cursorCell = buffer.GetCell(buffer.CursorRow, buffer.CursorCol);
            if (cursorCell.Character != '\0' && cursorCell.Character != ' ')
            {
                DrawTextRun(context, cursorCell.Character.ToString(),
                    cursorX, cursorY,
                    Colors.Black, cursorCell.Bold);
            }
        }

        // Clear dirty rows
        buffer.DirtyRows.Clear();
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
            InputReceived?.Invoke(e.Text);
            e.Handled = true;
        }
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
            InputReceived?.Invoke(data);
            e.Handled = true;
        }
    }
}
