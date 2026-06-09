using System;
using System.Collections.Generic;
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

    public void PutChar(char c)
    {
        if (CursorRow >= Rows) return;
        if (CursorCol >= Columns)
        {
            CursorCol = 0;
            LineFeed();
        }

        _cells[CursorRow, CursorCol] = new TerminalCell
        {
            Character = c,
            Foreground = CurrentForeground,
            Background = CurrentBackground,
            Bold = CurrentBold,
            Underline = CurrentUnderline
        };
        DirtyRows.Add(CursorRow);
        CursorCol++;
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
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c] = TerminalCell.Default;
            }
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
            for (int c = CursorCol; c < Columns; c++)
            {
                _cells[CursorRow, c] = TerminalCell.Default;
            }
            DirtyRows.Add(CursorRow);
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

        var newCells = new TerminalCell[newRows, newColumns];

        // Initialize new cells
        for (int r = 0; r < newRows; r++)
        {
            for (int c = 0; c < newColumns; c++)
            {
                newCells[r, c] = TerminalCell.Default;
            }
        }

        // Copy existing content
        int copyRows = Math.Min(Rows, newRows);
        int copyCols = Math.Min(Columns, newColumns);
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

        // Mark all rows dirty
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);

        Changed?.Invoke();
    }

    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
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
