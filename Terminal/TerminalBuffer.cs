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

    public HashSet<int> DirtyRows { get; } = new();

    // Current text attributes
    public Color CurrentForeground { get; set; } = TerminalColors.DefaultForeground;
    public Color CurrentBackground { get; set; } = TerminalColors.DefaultBackground;
    public bool CurrentBold { get; set; }
    public bool CurrentUnderline { get; set; }

    public event Action? Changed;

    public TerminalBuffer(int columns = 80, int rows = 24, int maxScrollback = 10000)
    {
        Columns = columns;
        Rows = rows;
        _maxScrollback = maxScrollback;
        _cells = new TerminalCell[rows, columns];
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
            CursorCol = 0;
            LineFeed();
        }
        if (width == 2 && CursorCol == Columns - 1)
        {
            CursorCol = 0;
            LineFeed();
        }

        ClearWideContext(CursorRow, CursorCol);
        _cells[CursorRow, CursorCol] = new TerminalCell
        {
            Character = c,
            Foreground = CurrentForeground,
            Background = CurrentBackground,
            Bold = CurrentBold,
            Underline = CurrentUnderline,
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
                IsWideContinuation = true
            };
        }

        DirtyRows.Add(CursorRow);
        CursorCol += width;
    }

    private void ClearWideContext(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return;

        if (_cells[row, col].IsWideContinuation && col > 0)
            _cells[row, col - 1] = TerminalCell.Default;

        if (col + 1 < Columns && _cells[row, col + 1].IsWideContinuation)
            _cells[row, col + 1] = TerminalCell.Default;

        _cells[row, col] = TerminalCell.Default;
    }

    private static int GetDisplayWidth(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format)
            return 0;

        return IsWideCharacter(c) ? 2 : 1;
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

    public void LineFeed()
    {
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
        if (_scrollback.Count >= _maxScrollback)
        {
            _scrollback.RemoveAt(0);
        }

        var topRow = new TerminalCell[Columns];
        for (int c = 0; c < Columns; c++)
        {
            topRow[c] = _cells[0, c];
        }
        _scrollback.Add(topRow);

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
            _cells[Rows - 1, c] = TerminalCell.Default;
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
            _cells[0, c] = TerminalCell.Default;
        }
        DirtyRows.Add(0);
        Changed?.Invoke();
    }

    public void ClearScreen()
    {
        for (int r = 0; r < _cells.GetLength(0); r++)
        {
            for (int c = 0; c < _cells.GetLength(1); c++)
            {
                _cells[r, c] = TerminalCell.Default;
            }
            if (r < Rows)
                DirtyRows.Add(r);
        }
        Changed?.Invoke();
    }

    public void ClearLine()
    {
        if (CursorRow >= 0 && CursorRow < Rows)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[CursorRow, c] = TerminalCell.Default;
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
                _cells[CursorRow, c] = TerminalCell.Default;
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
            _cells[CursorRow, c] = TerminalCell.Default;

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
            _cells[CursorRow, c] = TerminalCell.Default;

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
            _cells[CursorRow, destCol++] = TerminalCell.Default;

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
            _cells[row, 0] = TerminalCell.Default;

        for (var col = 1; col < Columns; col++)
        {
            if (_cells[row, col].IsWideContinuation && _cells[row, col - 1].IsWideContinuation)
                _cells[row, col] = TerminalCell.Default;
        }

        if (Columns > 1 && _cells[row, Columns - 1].IsWideContinuation)
        {
            _cells[row, Columns - 2] = TerminalCell.Default;
            _cells[row, Columns - 1] = TerminalCell.Default;
        }
    }

    public void ClearToEndOfScreen()
    {
        ClearToEndOfLine();
        for (int r = CursorRow + 1; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c] = TerminalCell.Default;
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
                newCells[r, c] = TerminalCell.Default;
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
                _cells[r, c] = TerminalCell.Default;
            }
        }
        CursorRow = 0;
        CursorCol = 0;
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);
    }

    public void ResetAttributes()
    {
        CurrentForeground = TerminalColors.DefaultForeground;
        CurrentBackground = TerminalColors.DefaultBackground;
        CurrentBold = false;
        CurrentUnderline = false;
    }

    public int ScrollbackCount => _scrollback.Count;

    public void MarkAllDirty()
    {
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);
    }
}
