using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;

namespace ChiXueSsh.Terminal;

public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private readonly List<TerminalCell[]> _scrollback = new();
    private readonly int _maxScrollback;

    public int Rows { get; private set; }
    public int Columns { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;
    public bool PushClearedScreenToScrollback { get; set; } = true;
    public bool TreatAmbiguousAsWide { get; set; }
    public bool AutoWrapMode { get; set; } = true;
    public bool OriginMode { get; set; }
    public bool ReverseVideoMode { get; set; }
    public bool NewLineMode { get; set; }
    public bool InsertMode { get; set; }
    public bool CursorKeyApplicationMode { get; set; }
    public bool NumericKeypadApplicationMode { get; set; }
    public bool ClearScreenWithDefaultBackground { get; set; } = true;
    public bool DisableAlternateScreen { get; set; }
    public bool DisableBlinkingText { get; set; }
    public bool DisableTitleChange { get; set; }
    public bool DisableTerminalPrint { get; set; }
    public bool IgnoreResizeRequest { get; set; } = true;
    public bool UseBuiltinLineDrawing { get; set; } = true;
    public bool UseBuiltinPowerline { get; set; } = true;
    public Color DefaultForegroundColor { get; set; } = TerminalColors.DefaultForeground;
    public Color DefaultBackgroundColor { get; set; } = TerminalColors.DefaultBackground;
    public Color BoldForegroundColor { get; set; } = Color.Parse("#33FF33");
    public Color[] AnsiColors { get; set; } = TerminalColors.Standard16.ToArray();

    public HashSet<int> DirtyRows { get; } = new();

    // Current text attributes
    public Color CurrentForeground { get; set; } = TerminalColors.DefaultForeground;
    public Color CurrentBackground { get; set; } = TerminalColors.DefaultBackground;
    public bool CurrentBold { get; set; }
    public bool CurrentUnderline { get; set; }
    public bool CurrentBlinking { get; set; }

    public event Action? Changed;

    public TerminalBuffer(
        int columns = 80,
        int rows = 24,
        int maxScrollback = 10000,
        bool pushClearedScreenToScrollback = true,
        bool treatAmbiguousAsWide = false,
        bool autoWrapMode = true,
        bool originMode = false,
        bool reverseVideoMode = false,
        bool newLineMode = false,
        bool insertMode = false,
        bool cursorKeyApplicationMode = false,
        bool numericKeypadApplicationMode = false,
        bool clearScreenWithDefaultBackground = true,
        bool disableAlternateScreen = false,
        bool disableBlinkingText = false,
        bool disableTitleChange = false,
        bool disableTerminalPrint = false,
        bool ignoreResizeRequest = true,
        bool useBuiltinLineDrawing = true,
        bool useBuiltinPowerline = true,
        Color? defaultForegroundColor = null,
        Color? defaultBackgroundColor = null,
        Color? boldForegroundColor = null,
        Color[]? ansiColors = null)
    {
        Columns = columns;
        Rows = rows;
        _maxScrollback = maxScrollback;
        PushClearedScreenToScrollback = pushClearedScreenToScrollback;
        TreatAmbiguousAsWide = treatAmbiguousAsWide;
        AutoWrapMode = autoWrapMode;
        OriginMode = originMode;
        ReverseVideoMode = reverseVideoMode;
        NewLineMode = newLineMode;
        InsertMode = insertMode;
        CursorKeyApplicationMode = cursorKeyApplicationMode;
        NumericKeypadApplicationMode = numericKeypadApplicationMode;
        ClearScreenWithDefaultBackground = clearScreenWithDefaultBackground;
        DisableAlternateScreen = disableAlternateScreen;
        DisableBlinkingText = disableBlinkingText;
        DisableTitleChange = disableTitleChange;
        DisableTerminalPrint = disableTerminalPrint;
        IgnoreResizeRequest = ignoreResizeRequest;
        UseBuiltinLineDrawing = useBuiltinLineDrawing;
        UseBuiltinPowerline = useBuiltinPowerline;
        DefaultForegroundColor = defaultForegroundColor ?? TerminalColors.DefaultForeground;
        DefaultBackgroundColor = defaultBackgroundColor ?? TerminalColors.DefaultBackground;
        BoldForegroundColor = boldForegroundColor ?? Color.Parse("#33FF33");
        AnsiColors = ansiColors is { Length: >= 16 } ? ansiColors.Take(16).ToArray() : TerminalColors.Standard16.ToArray();
        _cells = new TerminalCell[rows, columns];
        ResetAttributes();
        Clear();
    }

    public TerminalCell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return TerminalCell.Default;
        return _cells[row, col];
    }

    public TerminalCell GetViewportCell(int row, int col, int scrollOffset)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return TerminalCell.Default;

        scrollOffset = Math.Clamp(scrollOffset, 0, _scrollback.Count);
        var combinedRow = _scrollback.Count - scrollOffset + row;
        if (combinedRow < 0)
            return TerminalCell.Default;

        if (combinedRow < _scrollback.Count)
        {
            var scrollbackRow = _scrollback[combinedRow];
            return col < scrollbackRow.Length
                ? scrollbackRow[col]
                : TerminalCell.Default;
        }

        var screenRow = combinedRow - _scrollback.Count;
        return screenRow >= 0 && screenRow < Rows
            ? _cells[screenRow, col]
            : TerminalCell.Default;
    }

    public void PutChar(char c)
    {
        if (CursorRow >= Rows) return;
        var width = GetDisplayWidth(c);
        if (width == 0) return;

        if (CursorCol >= Columns)
        {
            if (AutoWrapMode)
            {
                CursorCol = 0;
                LineFeed();
            }
            else
            {
                CursorCol = Columns - 1;
            }
        }
        if (width == 2 && CursorCol == Columns - 1)
        {
            if (AutoWrapMode)
            {
                CursorCol = 0;
                LineFeed();
            }
            else
            {
                width = 1;
            }
        }

        if (InsertMode)
            InsertBlankCharacters(width);

        ClearWideContext(CursorRow, CursorCol);
        _cells[CursorRow, CursorCol] = new TerminalCell
        {
            Character = c,
            Foreground = CurrentForeground,
            Background = CurrentBackground,
            Bold = CurrentBold,
            Underline = CurrentUnderline,
            Blinking = CurrentBlinking,
            IsWideContinuation = false
        };
        if (width == 2 && CursorCol + 1 < Columns)
        {
            ClearWideContext(CursorRow, CursorCol + 1);
            _cells[CursorRow, CursorCol + 1] = new TerminalCell
            {
                Character = ' ',
                Foreground = CurrentForeground,
                Background = CurrentBackground,
                Bold = CurrentBold,
                Underline = CurrentUnderline,
                Blinking = CurrentBlinking,
                IsWideContinuation = true
            };
        }

        DirtyRows.Add(CursorRow);
        CursorCol = AutoWrapMode ? CursorCol + width : Math.Min(Columns - 1, CursorCol + width);
    }

    private void ClearWideContext(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return;

        if (_cells[row, col].IsWideContinuation && col > 0)
            _cells[row, col - 1] = CreateClearedCell();

        if (col + 1 < Columns && _cells[row, col + 1].IsWideContinuation)
            _cells[row, col + 1] = CreateClearedCell();

        _cells[row, col] = CreateClearedCell();
    }

    private int GetDisplayWidth(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format)
            return 0;

        return IsWideCharacter(c) || TreatAmbiguousAsWide && IsAmbiguousWidthCharacter(c) ? 2 : 1;
    }

    private static bool IsWideCharacter(char c)
    {
        var code = c;
        return code >= 0x1100 && code <= 0x115F
            || code >= 0x2329 && code <= 0x232A
            || code >= 0x2E80 && code <= 0xA4CF
            || code >= 0xAC00 && code <= 0xD7A3
            || code >= 0xF900 && code <= 0xFAFF
            || code >= 0xFE10 && code <= 0xFE19
            || code >= 0xFE30 && code <= 0xFE6F
            || code >= 0xFF00 && code <= 0xFF60
            || code >= 0xFFE0 && code <= 0xFFE6;
    }

    private static bool IsAmbiguousWidthCharacter(char c)
    {
        var code = c;
        return code >= 0x00A1 && code <= 0x00FF
            || code >= 0x0101 && code <= 0x0111
            || code >= 0x0113 && code <= 0x11FF
            || code >= 0x2010 && code <= 0x2027
            || code >= 0x2030 && code <= 0x205E
            || code >= 0x2070 && code <= 0x209F
            || code >= 0x20A0 && code <= 0x20CF
            || code >= 0x2100 && code <= 0x214F
            || code >= 0x2150 && code <= 0x218F
            || code >= 0x2190 && code <= 0x21FF
            || code >= 0x2200 && code <= 0x22FF
            || code >= 0x2300 && code <= 0x23FF
            || code >= 0x2460 && code <= 0x24FF
            || code >= 0x2500 && code <= 0x257F
            || code >= 0x2580 && code <= 0x259F
            || code >= 0x25A0 && code <= 0x25FF
            || code >= 0x2600 && code <= 0x26FF
            || code >= 0x2700 && code <= 0x27BF
            || code >= 0x2900 && code <= 0x297F
            || code >= 0x2980 && code <= 0x29FF
            || code >= 0x2B00 && code <= 0x2BFF
            || code >= 0xE000 && code <= 0xF8FF;
    }

    public void LineFeed()
    {
        if (NewLineMode)
            CarriageReturn();

        CursorRow++;
        if (CursorRow >= Rows)
        {
            ScrollUp();
            CursorRow = Rows - 1;
        }
        DirtyRows.Add(CursorRow);
    }

    public void CarriageReturn()
    {
        CursorCol = 0;
    }

    public void Backspace()
    {
        // BS (\x08) only moves cursor left, does NOT erase the character
        if (CursorCol > 0)
        {
            CursorCol--;
            DirtyRows.Add(CursorRow);
        }
    }

    public void Tab()
    {
        int nextTab = ((CursorCol / 8) + 1) * 8;
        CursorCol = Math.Min(nextTab, Columns - 1);
    }

    public void ScrollUp()
    {
        // Save top row to scroll back
        AddScrollbackRow(0, includeBlank: true);

        // Shift all rows up
        for (int r = 0; r < Rows - 1; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c] = _cells[r + 1, c];
            }
            DirtyRows.Add(r);
        }

        // Clear bottom row
        for (int c = 0; c < Columns; c++)
        {
            _cells[Rows - 1, c] = CreateClearedCell();
        }
        DirtyRows.Add(Rows - 1);
        Changed?.Invoke();
    }

    public void ScrollDown()
    {
        for (int r = Rows - 1; r > 0; r--)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c] = _cells[r - 1, c];
            }
            DirtyRows.Add(r);
        }

        for (int c = 0; c < Columns; c++)
        {
            _cells[0, c] = CreateClearedCell();
        }
        DirtyRows.Add(0);
        Changed?.Invoke();
    }

    public void ClearScreen(bool clearScrollback = false)
    {
        if (clearScrollback)
            _scrollback.Clear();
        else if (PushClearedScreenToScrollback)
            PushVisibleScreenToScrollback();

        ClearScreenCells();
    }

    private void ClearScreenCells()
    {
        for (int r = 0; r < _cells.GetLength(0); r++)
        {
            for (int c = 0; c < _cells.GetLength(1); c++)
            {
                _cells[r, c] = CreateClearedCell();
            }
            if (r < Rows)
                DirtyRows.Add(r);
        }
        Changed?.Invoke();
    }

    private void PushVisibleScreenToScrollback()
    {
        for (var row = 0; row < Rows; row++)
            AddScrollbackRow(row, includeBlank: false);
    }

    private void AddScrollbackRow(int row, bool includeBlank)
    {
        if (_maxScrollback <= 0 || row < 0 || row >= Rows)
            return;

        if (!includeBlank && IsRowBlank(row))
            return;

        if (_scrollback.Count >= _maxScrollback)
            _scrollback.RemoveAt(0);

        var scrollbackRow = new TerminalCell[Columns];
        for (int c = 0; c < Columns; c++)
            scrollbackRow[c] = _cells[row, c];

        _scrollback.Add(scrollbackRow);
    }

    private bool IsRowBlank(int row)
    {
        for (var c = 0; c < Columns; c++)
        {
            var cell = _cells[row, c];
            if (cell.Character != ' ' ||
                cell.Background != TerminalColors.DefaultBackground ||
                cell.IsWideContinuation)
            {
                return false;
            }
        }

        return true;
    }

    public void ClearLine()
    {
        if (CursorRow >= 0 && CursorRow < Rows)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[CursorRow, c] = CreateClearedCell();
            }
            DirtyRows.Add(CursorRow);
        }
    }

    public void ClearToEndOfLine()
    {
        if (CursorRow >= 0 && CursorRow < Rows)
        {
            var startCol = CursorCol;
            if (startCol > 0 && startCol < Columns && _cells[CursorRow, startCol].IsWideContinuation)
                startCol--;

            for (int c = startCol; c < Columns; c++)
            {
                _cells[CursorRow, c] = CreateClearedCell();
            }
            DirtyRows.Add(CursorRow);
        }
    }

    public void EraseCharacters(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows)
            return;

        var startCol = GetWideSafeStartColumn(CursorRow, CursorCol);
        var endCol = Math.Min(Columns, CursorCol + Math.Max(1, count));
        if (endCol < Columns && _cells[CursorRow, endCol].IsWideContinuation)
            endCol++;

        for (int c = startCol; c < endCol; c++)
            _cells[CursorRow, c] = CreateClearedCell();

        DirtyRows.Add(CursorRow);
    }

    public void InsertBlankCharacters(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows)
            return;

        count = Math.Clamp(count, 1, Columns);
        var startCol = GetWideSafeStartColumn(CursorRow, CursorCol);
        for (int c = Columns - 1; c >= startCol + count; c--)
            _cells[CursorRow, c] = _cells[CursorRow, c - count];

        for (int c = startCol; c < Math.Min(Columns, startCol + count); c++)
            _cells[CursorRow, c] = CreateClearedCell();

        RepairWideBoundaries(CursorRow);
        DirtyRows.Add(CursorRow);
    }

    public void DeleteCharacters(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows)
            return;

        count = Math.Clamp(count, 1, Columns);
        var startCol = GetWideSafeStartColumn(CursorRow, CursorCol);
        var sourceCol = Math.Min(Columns, startCol + count);
        if (sourceCol < Columns && _cells[CursorRow, sourceCol].IsWideContinuation)
            sourceCol++;

        var destCol = startCol;
        while (sourceCol < Columns)
            _cells[CursorRow, destCol++] = _cells[CursorRow, sourceCol++];

        while (destCol < Columns)
            _cells[CursorRow, destCol++] = CreateClearedCell();

        RepairWideBoundaries(CursorRow);
        DirtyRows.Add(CursorRow);
    }

    private int GetWideSafeStartColumn(int row, int col)
    {
        if (row >= 0 && row < Rows && col > 0 && col < Columns && _cells[row, col].IsWideContinuation)
            return col - 1;

        return Math.Clamp(col, 0, Columns - 1);
    }

    private void RepairWideBoundaries(int row)
    {
        if (row < 0 || row >= Rows)
            return;

        if (_cells[row, 0].IsWideContinuation)
            _cells[row, 0] = CreateClearedCell();

        for (var col = 1; col < Columns; col++)
        {
            if (_cells[row, col].IsWideContinuation && _cells[row, col - 1].IsWideContinuation)
                _cells[row, col] = CreateClearedCell();
        }

        if (Columns > 1 && _cells[row, Columns - 1].IsWideContinuation)
        {
            _cells[row, Columns - 2] = CreateClearedCell();
            _cells[row, Columns - 1] = CreateClearedCell();
        }
    }

    public void ClearToEndOfScreen()
    {
        ClearToEndOfLine();
        for (int r = CursorRow + 1; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c] = CreateClearedCell();
            }
            DirtyRows.Add(r);
        }
        Changed?.Invoke();
    }

    public void MoveCursor(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Columns - 1);
    }

    public void MoveCursorUp(int n)
    {
        CursorRow = Math.Max(0, CursorRow - n);
    }

    public void MoveCursorDown(int n)
    {
        CursorRow = Math.Min(Rows - 1, CursorRow + n);
    }

    public void MoveCursorForward(int n)
    {
        CursorCol = Math.Min(Columns - 1, CursorCol + n);
    }

    public void MoveCursorBack(int n)
    {
        CursorCol = Math.Max(0, CursorCol - n);
    }

    public void Resize(int newColumns, int newRows)
    {
        if (newColumns == Columns && newRows == Rows)
            return;

        var allocatedRows = Math.Max(newRows, _cells.GetLength(0));
        var allocatedColumns = Math.Max(newColumns, _cells.GetLength(1));
        var newCells = new TerminalCell[allocatedRows, allocatedColumns];

        // Initialize new cells
        for (int r = 0; r < allocatedRows; r++)
        {
            for (int c = 0; c < allocatedColumns; c++)
            {
                newCells[r, c] = CreateClearedCell();
            }
        }

        // Copy the full allocated backing store. The logical Rows/Columns may shrink,
        // but hidden right/bottom cells are kept so a later resize can reveal them.
        int copyRows = Math.Min(_cells.GetLength(0), allocatedRows);
        int copyCols = Math.Min(_cells.GetLength(1), allocatedColumns);
        for (int r = 0; r < copyRows; r++)
        {
            for (int c = 0; c < copyCols; c++)
            {
                newCells[r, c] = _cells[r, c];
            }
        }

        _cells = newCells;
        Rows = newRows;
        Columns = newColumns;

        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, Columns - 1);

        for (int r = 0; r < Rows; r++)
            RepairWideBoundaries(r);

        // Mark all rows dirty
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);

        Changed?.Invoke();
    }

    public void Clear()
    {
        for (int r = 0; r < _cells.GetLength(0); r++)
        {
            for (int c = 0; c < _cells.GetLength(1); c++)
            {
                _cells[r, c] = CreateClearedCell();
            }
        }
        CursorRow = 0;
        CursorCol = 0;
        _scrollback.Clear();
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);
    }

    public void ResetAttributes()
    {
        CurrentForeground = DefaultForegroundColor;
        CurrentBackground = DefaultBackgroundColor;
        CurrentBold = false;
        CurrentUnderline = false;
        CurrentBlinking = false;
    }

    public Color GetAnsiColor(int index)
    {
        return index >= 0 && index < AnsiColors.Length
            ? AnsiColors[index]
            : TerminalColors.Get256Color(index);
    }

    private TerminalCell CreateClearedCell()
    {
        if (ClearScreenWithDefaultBackground)
            return CreateDefaultCell();

        return new TerminalCell
        {
            Character = ' ',
            Foreground = DefaultForegroundColor,
            Background = CurrentBackground,
            Bold = false,
            Underline = false,
            Blinking = false,
            IsWideContinuation = false
        };
    }

    public TerminalCell CreateDefaultCell()
    {
        return new TerminalCell
        {
            Character = ' ',
            Foreground = DefaultForegroundColor,
            Background = DefaultBackgroundColor,
            Bold = false,
            Underline = false,
            Blinking = false,
            IsWideContinuation = false
        };
    }

    public int ScrollbackCount => _scrollback.Count;

    public void MarkAllDirty()
    {
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);
    }
}
