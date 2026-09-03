using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Media;

namespace CxShell.Terminal;

public readonly record struct TerminalTextMatch(int Row, int Column, int Length);

public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private List<TerminalCell[]> _scrollback = new();
    private readonly int _maxScrollback;
    private TerminalCell[,]? _mainCellsWhileAlternate;
    private List<TerminalCell[]>? _mainScrollbackWhileAlternate;
    private int _mainCursorRowWhileAlternate;
    private int _mainCursorColWhileAlternate;
    private int _scrollTop;
    private int _scrollBottom;
    private bool[] _tabStops = [];
    private bool[] _wrappedRows;
    private bool[]? _mainWrappedRowsWhileAlternate;
    private List<bool> _scrollbackWrapped = new();
    private List<bool>? _mainScrollbackWrappedWhileAlternate;
    private int _mainKittyKeyboardFlagsWhileAlternate;
    private List<int>? _mainKittyKeyboardFlagStackWhileAlternate;
    private List<int> _kittyKeyboardFlagStack = new();
    private int _updateDepth;
    private bool _changePending;

    public int Rows { get; private set; }
    public int Columns { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool IsAlternateScreen { get; private set; }
    public int ScrollTop => _scrollTop;
    public int ScrollBottom => _scrollBottom;
    public bool CursorVisible { get; set; } = true;
    public int CursorStyle { get; private set; }
    public bool HasRemoteCursorStyle { get; private set; }
    public bool PushClearedScreenToScrollback { get; set; } = true;
    public bool TreatAmbiguousAsWide { get; set; }
    public bool AutoWrapMode { get; set; } = true;
    public bool OriginMode { get; set; }
    public bool ReverseVideoMode { get; set; }
    public bool NewLineMode { get; set; }
    public bool InsertMode { get; set; }
    public bool CursorKeyApplicationMode { get; set; }
    public bool NumericKeypadApplicationMode { get; set; }
    public bool BracketedPasteMode { get; set; }
    public bool FocusReportingMode { get; set; }
    public int ModifyOtherKeysMode { get; set; }
    public int KittyKeyboardFlags { get; private set; }
    public bool SynchronizedOutputMode { get; private set; }
    public TerminalMouseTracking MouseTracking { get; set; }
    public TerminalMouseEncoding MouseEncoding { get; set; }
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
    public bool UseBoldColor { get; set; } = true;
    public bool UseBoldFont { get; set; } = true;
    public Color[] AnsiColors { get; set; } = TerminalColors.Standard16.ToArray();

    public HashSet<int> DirtyRows { get; } = new();

    // Current text attributes
    public Color CurrentForeground { get; set; } = TerminalColors.DefaultForeground;
    public Color CurrentBackground { get; set; } = TerminalColors.DefaultBackground;
    public bool CurrentBoldIntensity { get; set; }
    public bool CurrentBold { get; set; }
    public bool CurrentDim { get; set; }
    public bool CurrentItalic { get; set; }
    public bool CurrentUnderline { get; set; }
    public bool CurrentDoubleUnderline { get; set; }
    public bool CurrentBlinking { get; set; }
    public bool CurrentReverse { get; set; }
    public bool CurrentInvisible { get; set; }
    public bool CurrentStrikethrough { get; set; }
    public string? CurrentHyperlinkUri { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Coalesces screen-change notifications while one received terminal data
    /// chunk is being parsed. This keeps large output from scheduling one UI
    /// invalidation per scroll operation.
    /// </summary>
    public void BeginUpdate() => _updateDepth++;

    public void EndUpdate()
    {
        if (_updateDepth == 0)
            return;

        _updateDepth--;
        RaisePendingChangeIfPossible();
    }

    public void SetHyperlink(string? uri)
    {
        CurrentHyperlinkUri = string.IsNullOrWhiteSpace(uri) ? null : uri;
    }

    public void ResetInputHyperlink() => CurrentHyperlinkUri = null;

    /// <summary>
    /// Applies the Kitty keyboard progressive-enhancement flags. Mode 1
    /// replaces the current flags, mode 2 sets bits, and mode 3 clears bits.
    /// </summary>
    public void ApplyKittyKeyboardFlags(int flags, int mode = 1)
    {
        flags = Math.Clamp(flags, 0, 31);
        mode = Math.Clamp(mode, 1, 3);
        KittyKeyboardFlags = mode switch
        {
            2 => KittyKeyboardFlags | flags,
            3 => KittyKeyboardFlags & ~flags,
            _ => flags
        };
    }

    /// <summary>Pushes the current Kitty keyboard flags onto a bounded stack.</summary>
    public void PushKittyKeyboardFlags(int flags)
    {
        if (_kittyKeyboardFlagStack.Count >= 16)
            _kittyKeyboardFlagStack.RemoveAt(0);

        _kittyKeyboardFlagStack.Add(KittyKeyboardFlags);
        KittyKeyboardFlags = Math.Clamp(flags, 0, 31);
    }

    /// <summary>Restores up to <paramref name="count"/> Kitty flag stack entries.</summary>
    public void PopKittyKeyboardFlags(int count = 1)
    {
        count = Math.Clamp(count, 1, 16);
        if (_kittyKeyboardFlagStack.Count == 0)
        {
            KittyKeyboardFlags = 0;
            return;
        }

        while (count-- > 0 && _kittyKeyboardFlagStack.Count > 0)
        {
            var last = _kittyKeyboardFlagStack.Count - 1;
            KittyKeyboardFlags = _kittyKeyboardFlagStack[last];
            _kittyKeyboardFlagStack.RemoveAt(last);
        }

        // Kitty defines popping the last stack entry as a reset, rather than
        // restoring the value that was stored in that entry.
        if (_kittyKeyboardFlagStack.Count == 0)
            KittyKeyboardFlags = 0;
    }

    /// <summary>Clears current Kitty flags and the current screen's stack.</summary>
    public void ResetKittyKeyboardFlags()
    {
        KittyKeyboardFlags = 0;
        _kittyKeyboardFlagStack.Clear();
    }

    private void RequestChange()
    {
        _changePending = true;
        RaisePendingChangeIfPossible();
    }

    private void RaisePendingChangeIfPossible()
    {
        if (_updateDepth > 0 || SynchronizedOutputMode || !_changePending)
            return;

        _changePending = false;
        Changed?.Invoke();
    }

    public void SetSynchronizedOutputMode(bool enabled)
    {
        if (SynchronizedOutputMode == enabled)
            return;

        SynchronizedOutputMode = enabled;
        if (!enabled)
            RequestChange();
    }

    public bool IsModeEnabled(bool isPrivate, int mode)
    {
        if (!isPrivate)
        {
            return mode switch
            {
                4 => InsertMode,
                12 => false,
                20 => NewLineMode,
                _ => false
            };
        }

        return mode switch
        {
            1 => CursorKeyApplicationMode,
            5 => ReverseVideoMode,
            6 => OriginMode,
            7 => AutoWrapMode,
            9 => MouseTracking == TerminalMouseTracking.X10,
            25 => CursorVisible,
            47 or 1047 or 1049 => IsAlternateScreen,
            66 => NumericKeypadApplicationMode,
            1000 => MouseTracking == TerminalMouseTracking.Normal,
            1002 => MouseTracking == TerminalMouseTracking.ButtonEvent,
            1003 => MouseTracking == TerminalMouseTracking.AnyEvent,
            1004 => FocusReportingMode,
            1006 => MouseEncoding == TerminalMouseEncoding.Sgr,
            1015 => MouseEncoding == TerminalMouseEncoding.Urxvt,
            2004 => BracketedPasteMode,
            2026 => SynchronizedOutputMode,
            _ => false
        };
    }

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
        Color[]? ansiColors = null,
        string? boldTextMode = null)
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
        ApplyBoldTextMode(boldTextMode);
        AnsiColors = ansiColors is { Length: >= 16 } ? ansiColors.Take(16).ToArray() : TerminalColors.Standard16.ToArray();
        _cells = new TerminalCell[rows, columns];
        _wrappedRows = new bool[rows];
        _tabStops = BuildDefaultTabStops(columns);
        _scrollBottom = rows - 1;
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

    /// <summary>
    /// Copies one visible viewport row into a caller-owned buffer. The render
    /// path uses this to avoid resolving the same cell repeatedly for
    /// backgrounds, highlights, text runs and decorations.
    /// </summary>
    public void CopyViewportRow(int row, int scrollOffset, TerminalCell[] destination)
    {
        if (destination.Length == 0)
            return;

        scrollOffset = Math.Clamp(scrollOffset, 0, _scrollback.Count);
        var combinedRow = _scrollback.Count - scrollOffset + row;
        for (var column = 0; column < destination.Length; column++)
        {
            destination[column] = combinedRow >= 0 && combinedRow < _scrollback.Count
                ? column < _scrollback[combinedRow].Length
                    ? _scrollback[combinedRow][column]
                    : TerminalCell.Default
                : combinedRow >= _scrollback.Count &&
                  combinedRow - _scrollback.Count >= 0 &&
                  combinedRow - _scrollback.Count < Rows &&
                  column < Columns
                    ? _cells[combinedRow - _scrollback.Count, column]
                    : TerminalCell.Default;
        }
    }

    public string ExportText()
    {
        var lines = new List<string>(_scrollback.Count + Rows);
        foreach (var row in _scrollback)
            lines.Add(FormatRowText(row));

        for (var row = 0; row < Rows; row++)
        {
            var cells = new TerminalCell[Columns];
            for (var column = 0; column < Columns; column++)
                cells[column] = _cells[row, column];

            lines.Add(FormatRowText(cells));
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return string.Join('\n', lines);
    }

    private static string FormatRowText(IReadOnlyList<TerminalCell> row)
    {
        var end = row.Count;
        while (end > 0 && !row[end - 1].IsWideContinuation && row[end - 1].GetText() == " ")
            end--;

        var text = new StringBuilder(end);
        for (var column = 0; column < end; column++)
        {
            var cell = row[column];
            if (!cell.IsWideContinuation)
                text.Append(cell.GetText());
        }

        return text.ToString().TrimEnd();
    }

    public IReadOnlyList<TerminalTextMatch> FindTextMatches(string query)
    {
        if (string.IsNullOrEmpty(query))
            return [];

        var matches = new List<TerminalTextMatch>();
        for (var row = 0; row < _scrollback.Count + Rows; row++)
        {
            var cells = new TerminalCell[Columns];
            if (row < _scrollback.Count)
            {
                var scrollbackRow = _scrollback[row];
                for (var column = 0; column < Columns && column < scrollbackRow.Length; column++)
                    cells[column] = scrollbackRow[column];
            }
            else
            {
                var screenRow = row - _scrollback.Count;
                for (var column = 0; column < Columns; column++)
                    cells[column] = _cells[screenRow, column];
            }

            var line = BuildSearchLine(cells);
            var start = 0;
            while (start < line.Length)
            {
                var index = line.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                var endIndex = index + query.Length;
                if (index < line.ColumnByTextOffset.Count && endIndex > index)
                {
                    var startColumn = line.ColumnByTextOffset[index];
                    var endColumn = line.ColumnEndByTextOffset[Math.Min(endIndex - 1, line.ColumnEndByTextOffset.Count - 1)];
                    matches.Add(new TerminalTextMatch(row, startColumn, Math.Max(1, endColumn - startColumn)));
                }
                start = index + Math.Max(1, query.Length);
            }
        }

        return matches;
    }

    private static SearchLine BuildSearchLine(IReadOnlyList<TerminalCell> row)
    {
        var text = new StringBuilder(row.Count);
        var columns = new List<int>();
        var columnEnds = new List<int>();
        for (var column = 0; column < row.Count; column++)
        {
            var cell = row[column];
            if (cell.IsWideContinuation)
                continue;

            var cellText = cell.GetText();
            var width = column + 1 < row.Count && row[column + 1].IsWideContinuation ? 2 : 1;
            foreach (var character in cellText)
            {
                text.Append(character);
                columns.Add(column);
                columnEnds.Add(column + width);
            }
        }

        return new SearchLine(text.ToString(), columns, columnEnds);
    }

    private sealed record SearchLine(
        string Text,
        List<int> ColumnByTextOffset,
        List<int> ColumnEndByTextOffset)
    {
        public int Length => Text.Length;

        public int IndexOf(string value, int startIndex, StringComparison comparison) =>
            Text.IndexOf(value, startIndex, comparison);
    }

    public void PutChar(char c)
    {
        PutRune(c);
    }

    public void PutRune(int rune)
    {
        if (CursorRow >= Rows) return;
        var text = char.ConvertFromUtf32(rune);
        var width = GetDisplayWidth(rune);
        if (width == 0)
        {
            AppendCombiningText(text);
            return;
        }

        if (CursorCol >= Columns)
        {
            if (AutoWrapMode)
            {
                _wrappedRows[CursorRow] = true;
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
                _wrappedRows[CursorRow] = true;
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
            Character = text[0],
            Text = text.Length > 1 ? text : null,
            Foreground = CurrentForeground,
            Background = CurrentBackground,
            Bold = CurrentBold,
            Dim = CurrentDim,
            Italic = CurrentItalic,
            Underline = CurrentUnderline,
            DoubleUnderline = CurrentDoubleUnderline,
            Blinking = CurrentBlinking,
            Reverse = CurrentReverse,
            Invisible = CurrentInvisible,
            Strikethrough = CurrentStrikethrough,
            HyperlinkUri = CurrentHyperlinkUri,
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
                Dim = CurrentDim,
                Italic = CurrentItalic,
                Underline = CurrentUnderline,
                DoubleUnderline = CurrentDoubleUnderline,
                Blinking = CurrentBlinking,
                Reverse = CurrentReverse,
                Invisible = CurrentInvisible,
                Strikethrough = CurrentStrikethrough,
                HyperlinkUri = CurrentHyperlinkUri,
                IsWideContinuation = true
            };
        }

        DirtyRows.Add(CursorRow);
        CursorCol = AutoWrapMode ? CursorCol + width : Math.Min(Columns - 1, CursorCol + width);
    }

    private void AppendCombiningText(string text)
    {
        if (CursorRow < 0 || CursorRow >= Rows || CursorCol <= 0)
            return;

        var column = CursorCol - 1;
        if (_cells[CursorRow, column].IsWideContinuation && column > 0)
            column--;

        var cell = _cells[CursorRow, column];
        if (cell.Character == '\0' || cell.Character == ' ' && cell.Text == null)
            return;

        cell.Text = cell.GetText() + text;
        _cells[CursorRow, column] = cell;
        DirtyRows.Add(CursorRow);
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

    private int GetDisplayWidth(int rune)
    {
        var text = char.ConvertFromUtf32(rune);
        var category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format)
            return 0;

        return IsWideCharacter(rune) || TreatAmbiguousAsWide && IsAmbiguousWidthCharacter(rune) ? 2 : 1;
    }

    private static bool IsWideCharacter(int rune)
    {
        var code = rune;
        return code >= 0x1100 && code <= 0x115F
            || code >= 0x2329 && code <= 0x232A
            || code >= 0x2E80 && code <= 0xA4CF
            || code >= 0xAC00 && code <= 0xD7A3
            || code >= 0xF900 && code <= 0xFAFF
            || code >= 0xFE10 && code <= 0xFE19
            || code >= 0xFE30 && code <= 0xFE6F
            || code >= 0xFF00 && code <= 0xFF60
            || code >= 0xFFE0 && code <= 0xFFE6
            || code >= 0x1F300 && code <= 0x1FAFF
            || code >= 0x20000 && code <= 0x3FFFD;
    }

    private static bool IsAmbiguousWidthCharacter(int rune)
    {
        var code = rune;
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

        if (CursorRow == _scrollBottom)
        {
            ScrollUp();
            CursorRow = _scrollBottom;
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
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
            return;
        }

        if (CursorRow > 0 && IsLikelyWrappedFromPreviousLine(CursorRow))
        {
            DirtyRows.Add(CursorRow);
            CursorRow--;
            CursorCol = Math.Max(0, Columns - 1);
            DirtyRows.Add(CursorRow);
        }
    }

    private bool IsLikelyWrappedFromPreviousLine(int row)
    {
        if (row <= 0 || row >= Rows)
            return false;

        for (var col = Columns - 1; col >= Math.Max(0, Columns - 4); col--)
        {
            if (_cells[row - 1, col].Character != ' ' || _cells[row - 1, col].IsWideContinuation)
                return true;
        }

        return false;
    }

    public void Tab()
    {
        for (var column = Math.Min(Columns - 1, CursorCol + 1); column < Columns; column++)
        {
            if (_tabStops[column])
            {
                MoveCursor(CursorRow, column);
                return;
            }
        }

        MoveCursor(CursorRow, Columns - 1);
    }

    public void SetTabStop()
    {
        if (Columns > 0)
            _tabStops[Math.Clamp(CursorCol, 0, Columns - 1)] = true;
    }

    public void ClearTabStopAtCursor()
    {
        if (Columns > 0)
            _tabStops[Math.Clamp(CursorCol, 0, Columns - 1)] = false;
    }

    public void ClearAllTabStops()
    {
        Array.Clear(_tabStops, 0, _tabStops.Length);
    }

    public void ResetTabStops()
    {
        _tabStops = BuildDefaultTabStops(Columns);
    }

    public void TabForward(int count)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
            Tab();
    }

    public void TabBackward(int count)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            var found = false;
            for (var column = Math.Min(Columns - 1, CursorCol - 1); column >= 0; column--)
            {
                if (_tabStops[column])
                {
                    MoveCursor(CursorRow, column);
                    found = true;
                    break;
                }
            }

            if (!found)
                MoveCursor(CursorRow, 0);
        }
    }

    public void ScrollUp()
    {
        var top = Math.Clamp(_scrollTop, 0, Rows - 1);
        var bottom = Math.Clamp(_scrollBottom, top, Rows - 1);
        var preserveScrollback = !IsAlternateScreen && top == 0 && bottom == Rows - 1;

        // Only a full-screen main-buffer scroll contributes to scrollback. A
        // TUI's local scroll region must never leak rows into the shell history.
        if (preserveScrollback)
            AddScrollbackRow(top, includeBlank: false);

        for (int r = top; r < bottom; r++)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = _cells[r + 1, c];

            _wrappedRows[r] = _wrappedRows[r + 1];

            DirtyRows.Add(r);
        }

        for (int c = 0; c < Columns; c++)
            _cells[bottom, c] = CreateClearedCell();
        _wrappedRows[bottom] = false;

        DirtyRows.Add(bottom);
        RequestChange();
    }

    public void ScrollDown()
    {
        var top = Math.Clamp(_scrollTop, 0, Rows - 1);
        var bottom = Math.Clamp(_scrollBottom, top, Rows - 1);
        for (int r = bottom; r > top; r--)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = _cells[r - 1, c];

            _wrappedRows[r] = _wrappedRows[r - 1];

            DirtyRows.Add(r);
        }

        for (int c = 0; c < Columns; c++)
            _cells[top, c] = CreateClearedCell();
        _wrappedRows[top] = false;

        DirtyRows.Add(top);
        RequestChange();
    }

    /// <summary>
    /// 设置 DECSTBM 垂直滚动区域。参数使用零基行号，调用方应先完成协议的 1 基转换。
    /// </summary>
    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (top >= bottom)
        {
            ResetScrollRegion();
            MoveCursorHome();
            return;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        MoveCursorHome();
    }

    public void ResetScrollRegion()
    {
        _scrollTop = 0;
        _scrollBottom = Math.Max(0, Rows - 1);
    }

    /// <summary>把光标移到当前坐标系的首页;原点模式下首页是滚动区顶部。</summary>
    public void MoveCursorHome()
    {
        MoveCursor(OriginMode ? _scrollTop : 0, 0);
    }

    /// <summary>按 CUP/HVP 的规则定位;原点模式下行号相对于滚动区顶部。</summary>
    public void MoveCursorPosition(int row, int col)
    {
        var absoluteRow = OriginMode
            ? Math.Clamp(row + _scrollTop, _scrollTop, _scrollBottom)
            : row;
        MoveCursor(absoluteRow, col);
    }

    /// <summary>按 VPA 的规则定位行号,保留当前列。</summary>
    public void MoveCursorVerticalAbsolute(int row)
    {
        var absoluteRow = OriginMode
            ? Math.Clamp(row + _scrollTop, _scrollTop, _scrollBottom)
            : row;
        MoveCursor(absoluteRow, CursorCol);
    }

    public void SetCursorStyle(int style)
    {
        CursorStyle = Math.Clamp(style, 0, 6);
        HasRemoteCursorStyle = true;
        RequestChange();
    }

    public void ResetCursorStyle()
    {
        CursorStyle = 0;
        HasRemoteCursorStyle = false;
    }

    /// <summary>DECALN 屏幕对齐测试:以当前属性将活动屏填充为大写 E。</summary>
    public void FillScreenWithCharacter(char character)
    {
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                _cells[row, column] = new TerminalCell
                {
                    Character = character,
                    Foreground = CurrentForeground,
                    Background = CurrentBackground,
                    Bold = CurrentBold,
                    Dim = CurrentDim,
                    Italic = CurrentItalic,
                    Underline = CurrentUnderline,
                    DoubleUnderline = CurrentDoubleUnderline,
                    Blinking = CurrentBlinking,
                    Reverse = CurrentReverse,
                    Invisible = CurrentInvisible,
                    Strikethrough = CurrentStrikethrough,
                    IsWideContinuation = false
                };
            }

            DirtyRows.Add(row);
        }

        RequestChange();
    }

    /// <summary>进入备用屏，保留主屏及其 scrollback，备用屏始终不产生 scrollback。</summary>
    public bool EnterAlternateScreen(bool saveCursor = true)
    {
        if (DisableAlternateScreen || IsAlternateScreen)
            return false;

        _ = saveCursor;

        _mainCellsWhileAlternate = _cells;
        _mainScrollbackWhileAlternate = _scrollback;
        _mainWrappedRowsWhileAlternate = _wrappedRows;
        _mainScrollbackWrappedWhileAlternate = _scrollbackWrapped;
        _mainKittyKeyboardFlagsWhileAlternate = KittyKeyboardFlags;
        _mainKittyKeyboardFlagStackWhileAlternate = _kittyKeyboardFlagStack;
        _mainCursorRowWhileAlternate = CursorRow;
        _mainCursorColWhileAlternate = CursorCol;

        _cells = new TerminalCell[Rows, Columns];
        _scrollback = new List<TerminalCell[]>();
        _wrappedRows = new bool[Rows];
        _scrollbackWrapped = new List<bool>();
        _kittyKeyboardFlagStack = new List<int>();
        KittyKeyboardFlags = 0;
        IsAlternateScreen = true;
        ResetScrollRegion();
        FillActiveCells();
        CursorRow = 0;
        CursorCol = 0;
        DirtyRows.Clear();
        for (var row = 0; row < Rows; row++)
            DirtyRows.Add(row);
        RequestChange();
        return true;
    }

    /// <summary>退出备用屏并恢复进入前的主屏内容、历史和光标位置。</summary>
    public bool ExitAlternateScreen()
    {
        if (!IsAlternateScreen)
            return false;

        _cells = _mainCellsWhileAlternate ?? _cells;
        _scrollback = _mainScrollbackWhileAlternate ?? new List<TerminalCell[]>();
        _wrappedRows = _mainWrappedRowsWhileAlternate ?? new bool[Rows];
        _scrollbackWrapped = _mainScrollbackWrappedWhileAlternate ?? new List<bool>();
        KittyKeyboardFlags = _mainKittyKeyboardFlagsWhileAlternate;
        _kittyKeyboardFlagStack = _mainKittyKeyboardFlagStackWhileAlternate ?? new List<int>();
        _mainCellsWhileAlternate = null;
        _mainScrollbackWhileAlternate = null;
        _mainWrappedRowsWhileAlternate = null;
        _mainScrollbackWrappedWhileAlternate = null;
        _mainKittyKeyboardFlagStackWhileAlternate = null;
        IsAlternateScreen = false;
        CursorRow = Math.Clamp(_mainCursorRowWhileAlternate, 0, Rows - 1);
        CursorCol = Math.Clamp(_mainCursorColWhileAlternate, 0, Columns - 1);
        ResetScrollRegion();
        DirtyRows.Clear();
        for (var row = 0; row < Rows; row++)
            DirtyRows.Add(row);
        RequestChange();
        return true;
    }

    private void FillActiveCells()
    {
        for (var row = 0; row < _cells.GetLength(0); row++)
        {
            for (var column = 0; column < _cells.GetLength(1); column++)
                _cells[row, column] = CreateClearedCell();
        }
    }

    public void ClearScreen(bool clearScrollback = false)
    {
        if (clearScrollback)
        {
            _scrollback.Clear();
            _scrollbackWrapped.Clear();
        }
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
            {
                _wrappedRows[r] = false;
                DirtyRows.Add(r);
            }
        }
        RequestChange();
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
        {
            _scrollback.RemoveAt(0);
            if (_scrollbackWrapped.Count > 0)
                _scrollbackWrapped.RemoveAt(0);
        }

        var scrollbackRow = new TerminalCell[Columns];
        for (int c = 0; c < Columns; c++)
            scrollbackRow[c] = _cells[row, c];

        _scrollback.Add(scrollbackRow);
        _scrollbackWrapped.Add(_wrappedRows[row]);
    }

    private bool IsRowBlank(int row)
    {
        for (var c = 0; c < Columns; c++)
        {
            var cell = _cells[row, c];
            if (cell.Character != ' ' || cell.IsWideContinuation)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsScrollbackRowBlank(TerminalCell[] row)
    {
        for (var c = 0; c < row.Length; c++)
        {
            var cell = row[c];
            if (cell.Character != ' ' || cell.IsWideContinuation)
            {
                return false;
            }
        }

        return true;
    }

    public int MaxMeaningfulScrollOffset
    {
        get
        {
            for (var i = 0; i < _scrollback.Count; i++)
            {
                if (!IsScrollbackRowBlank(_scrollback[i]))
                    return _scrollback.Count - i;
            }

            return 0;
        }
    }

    public void ClearLine()
    {
        if (CursorRow >= 0 && CursorRow < Rows)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[CursorRow, c] = CreateClearedCell();
            }
            _wrappedRows[CursorRow] = false;
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
            _wrappedRows[CursorRow] = false;
            DirtyRows.Add(CursorRow);
        }
    }

    public void ClearToBeginningOfLine()
    {
        if (CursorRow < 0 || CursorRow >= Rows)
            return;

        var endCol = Math.Clamp(CursorCol, 0, Columns - 1);
        if (endCol + 1 < Columns && _cells[CursorRow, endCol + 1].IsWideContinuation)
            endCol++;

        for (int c = 0; c <= endCol; c++)
            _cells[CursorRow, c] = CreateClearedCell();

        _wrappedRows[CursorRow] = false;

        DirtyRows.Add(CursorRow);
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
        var startRow = FindWrappedInputStartRow(CursorRow);
        for (int r = startRow; r < CursorRow; r++)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = CreateClearedCell();

            DirtyRows.Add(r);
        }

        ClearToEndOfLine();
        for (int r = CursorRow + 1; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                _cells[r, c] = CreateClearedCell();
            }
            DirtyRows.Add(r);
        }
        RequestChange();
    }

    private int FindWrappedInputStartRow(int row)
    {
        row = Math.Clamp(row, 0, Rows - 1);
        while (row > 0 && IsLikelyContinuationRow(row))
            row--;

        return row;
    }

    private bool IsLikelyContinuationRow(int row)
    {
        if (row <= 0 || row >= Rows)
            return false;

        var previous = row - 1;
        var previousLastNonBlank = -1;
        for (var col = Columns - 1; col >= 0; col--)
        {
            if (_cells[previous, col].Character != ' ' || _cells[previous, col].IsWideContinuation)
            {
                previousLastNonBlank = col;
                break;
            }
        }

        if (previousLastNonBlank < Math.Max(0, Columns - 4))
            return false;

        for (var col = 0; col < Math.Min(Columns, 16); col++)
        {
            if (_cells[row, col].Character != ' ' || _cells[row, col].IsWideContinuation)
                return true;
        }

        return false;
    }

    public void ClearToBeginningOfScreen()
    {
        for (int r = 0; r < CursorRow; r++)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = CreateClearedCell();

            DirtyRows.Add(r);
        }

        ClearToBeginningOfLine();
        RequestChange();
    }

    public void InsertLines(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows)
            return;

        var bottom = Math.Max(CursorRow, _scrollBottom);
        count = Math.Clamp(count, 1, bottom - CursorRow + 1);
        for (int r = bottom; r >= CursorRow + count; r--)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = _cells[r - count, c];

            _wrappedRows[r] = _wrappedRows[r - count];

            DirtyRows.Add(r);
        }

        for (int r = CursorRow; r < CursorRow + count; r++)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = CreateClearedCell();

            _wrappedRows[r] = false;
            DirtyRows.Add(r);
        }

        RequestChange();
    }

    public void DeleteLines(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows)
            return;

        var bottom = Math.Max(CursorRow, _scrollBottom);
        count = Math.Clamp(count, 1, bottom - CursorRow + 1);
        for (int r = CursorRow; r <= bottom - count; r++)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = _cells[r + count, c];

            _wrappedRows[r] = _wrappedRows[r + count];

            DirtyRows.Add(r);
        }

        for (int r = Math.Max(CursorRow, bottom - count + 1); r <= bottom; r++)
        {
            for (int c = 0; c < Columns; c++)
                _cells[r, c] = CreateClearedCell();

            _wrappedRows[r] = false;
            DirtyRows.Add(r);
        }

        RequestChange();
    }

    public void MoveCursor(int row, int col)
    {
        DirtyRows.Add(CursorRow);
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Columns - 1);
        DirtyRows.Add(CursorRow);
    }

    public void MoveCursorUp(int n)
    {
        DirtyRows.Add(CursorRow);
        CursorRow = Math.Max(OriginMode ? _scrollTop : 0, CursorRow - n);
        DirtyRows.Add(CursorRow);
    }

    public void MoveCursorDown(int n)
    {
        DirtyRows.Add(CursorRow);
        CursorRow = Math.Min(OriginMode ? _scrollBottom : Rows - 1, CursorRow + n);
        DirtyRows.Add(CursorRow);
    }

    public void MoveCursorForward(int n)
    {
        DirtyRows.Add(CursorRow);
        CursorCol = Math.Min(Columns - 1, CursorCol + n);
        DirtyRows.Add(CursorRow);
    }

    public void MoveCursorBack(int n)
    {
        DirtyRows.Add(CursorRow);
        CursorCol = Math.Max(0, CursorCol - n);
        DirtyRows.Add(CursorRow);
    }

    public void Resize(int newColumns, int newRows)
    {
        newColumns = Math.Max(1, newColumns);
        newRows = Math.Max(1, newRows);
        if (newColumns == Columns && newRows == Rows)
            return;

        if (!IsAlternateScreen)
            ResizePrimaryWithReflow(newColumns, newRows);
        else
        {
            ResizeGrid(newColumns, newRows);
            ResizeParkedMain(newColumns, newRows);
        }

        RequestChange();
    }

    private void ResizePrimaryWithReflow(int newColumns, int newRows)
    {
        var oldRows = Rows;
        var oldColumns = Columns;
        var totalRows = _scrollback.Count + oldRows;
        var lines = new List<ReflowLine>();
        var lineForRow = new int[totalRows];
        var offsetForRow = new int[totalRows];

        for (var combinedRow = 0; combinedRow < totalRows; combinedRow++)
        {
            var continued = combinedRow > 0 && IsRowWrapped(combinedRow - 1);
            if (!continued || lines.Count == 0)
                lines.Add(new ReflowLine());

            var line = lines[^1];
            lineForRow[combinedRow] = lines.Count - 1;
            offsetForRow[combinedRow] = line.Cells.Count;
            var row = GetCombinedRow(combinedRow);
            for (var column = 0; column < oldColumns; column++)
                line.Cells.Add(row[column]);
        }

        var cursorCombinedRow = _scrollback.Count + Math.Clamp(CursorRow, 0, oldRows - 1);
        var cursorLine = lineForRow[cursorCombinedRow];
        var cursorOffset = offsetForRow[cursorCombinedRow] + Math.Clamp(CursorCol, 0, oldColumns - 1);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var minimumLength = lineIndex == cursorLine ? cursorOffset : 0;
            var line = lines[lineIndex];
            while (line.Cells.Count > minimumLength &&
                   !line.Cells[^1].IsWideContinuation &&
                   line.Cells[^1].GetText() == " ")
            {
                line.Cells.RemoveAt(line.Cells.Count - 1);
            }
        }

        var reflowedRows = new List<TerminalCell[]>();
        var reflowedWraps = new List<bool>();
        var cursorOutputRow = 0;
        var cursorOutputColumn = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Cells.Count == 0)
            {
                reflowedRows.Add(CreateCellRow(newColumns));
                reflowedWraps.Add(false);
                continue;
            }

            var offset = 0;
            while (offset < line.Cells.Count)
            {
                var count = Math.Min(newColumns, line.Cells.Count - offset);
                if (offset + count < line.Cells.Count && count > 1 && line.Cells[offset + count].IsWideContinuation)
                    count--;

                if (count <= 0)
                    count = Math.Min(newColumns, line.Cells.Count - offset);

                var row = CreateCellRow(newColumns);
                line.Cells.CopyTo(offset, row, 0, Math.Min(count, row.Length));
                var outputRow = reflowedRows.Count;
                reflowedRows.Add(row);
                reflowedWraps.Add(offset + count < line.Cells.Count);

                if (lineIndex == cursorLine && cursorOffset >= offset &&
                    (cursorOffset < offset + count || cursorOffset == line.Cells.Count))
                {
                    cursorOutputRow = outputRow;
                    cursorOutputColumn = Math.Clamp(cursorOffset - offset, 0, newColumns - 1);
                }

                offset += count;
            }
        }

        while (reflowedRows.Count < newRows)
        {
            reflowedRows.Add(CreateCellRow(newColumns));
            reflowedWraps.Add(false);
        }

        var screenStart = Math.Max(0, reflowedRows.Count - newRows);
        var screenRows = reflowedRows.Skip(screenStart).Take(newRows).ToArray();
        var screenWraps = reflowedWraps.Skip(screenStart).Take(newRows).ToArray();
        var scrollRows = reflowedRows.Take(screenStart).ToList();
        var scrollWraps = reflowedWraps.Take(screenStart).ToList();

        Columns = newColumns;
        Rows = newRows;
        _cells = new TerminalCell[newRows, newColumns];
        _wrappedRows = new bool[newRows];
        for (var rowIndex = 0; rowIndex < newRows; rowIndex++)
        {
            for (var column = 0; column < newColumns; column++)
                _cells[rowIndex, column] = screenRows[rowIndex][column];
            _wrappedRows[rowIndex] = screenWraps[rowIndex];
        }

        _scrollback = scrollRows;
        _scrollbackWrapped = scrollWraps;
        TrimScrollback();
        CursorRow = Math.Clamp(cursorOutputRow - screenStart, 0, Rows - 1);
        CursorCol = Math.Clamp(cursorOutputColumn, 0, Columns - 1);
        _tabStops = ResizeTabStops(_tabStops, Columns);
        ResetScrollRegion();

        for (var rowIndex = 0; rowIndex < Rows; rowIndex++)
        {
            RepairWideBoundaries(rowIndex);
            DirtyRows.Add(rowIndex);
        }
    }

    private bool IsRowWrapped(int combinedRow)
    {
        if (combinedRow < _scrollback.Count)
            return combinedRow < _scrollbackWrapped.Count && _scrollbackWrapped[combinedRow];

        var screenRow = combinedRow - _scrollback.Count;
        return screenRow >= 0 && screenRow < _wrappedRows.Length && _wrappedRows[screenRow];
    }

    private TerminalCell[] GetCombinedRow(int combinedRow)
    {
        if (combinedRow < _scrollback.Count)
            return _scrollback[combinedRow];

        var screenRow = combinedRow - _scrollback.Count;
        var row = new TerminalCell[Columns];
        for (var column = 0; column < Columns; column++)
            row[column] = _cells[screenRow, column];
        return row;
    }

    private TerminalCell[] CreateCellRow(int columns)
    {
        var row = new TerminalCell[columns];
        for (var column = 0; column < columns; column++)
            row[column] = CreateClearedCell();
        return row;
    }

    private void TrimScrollback()
    {
        while (_scrollback.Count > _maxScrollback)
        {
            _scrollback.RemoveAt(0);
            if (_scrollbackWrapped.Count > 0)
                _scrollbackWrapped.RemoveAt(0);
        }
    }

    private void ResizeGrid(int newColumns, int newRows)
    {
        var oldCells = _cells;
        var oldWraps = _wrappedRows;
        var oldRows = oldCells.GetLength(0);
        var oldColumns = oldCells.GetLength(1);
        Columns = newColumns;
        Rows = newRows;
        _cells = new TerminalCell[newRows, newColumns];
        _wrappedRows = new bool[newRows];
        for (var row = 0; row < newRows; row++)
        {
            for (var column = 0; column < newColumns; column++)
                _cells[row, column] = CreateClearedCell();
        }

        for (var row = 0; row < Math.Min(oldRows, newRows); row++)
        {
            for (var column = 0; column < Math.Min(oldColumns, newColumns); column++)
                _cells[row, column] = oldCells[row, column];
            _wrappedRows[row] = row < oldWraps.Length && oldWraps[row];
        }

        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, Columns - 1);
        _tabStops = ResizeTabStops(_tabStops, Columns);
        ResetScrollRegion();
        for (var row = 0; row < Rows; row++)
        {
            RepairWideBoundaries(row);
            DirtyRows.Add(row);
        }
    }

    private void ResizeParkedMain(int newColumns, int newRows)
    {
        if (_mainCellsWhileAlternate == null)
            return;

        var source = _mainCellsWhileAlternate;
        var resized = new TerminalCell[newRows, newColumns];
        for (var row = 0; row < newRows; row++)
        {
            for (var column = 0; column < newColumns; column++)
                resized[row, column] = CreateClearedCell();
        }

        for (var row = 0; row < Math.Min(source.GetLength(0), newRows); row++)
        {
            for (var column = 0; column < Math.Min(source.GetLength(1), newColumns); column++)
                resized[row, column] = source[row, column];
        }

        _mainCellsWhileAlternate = resized;
        var sourceWraps = _mainWrappedRowsWhileAlternate ?? [];
        _mainWrappedRowsWhileAlternate = new bool[newRows];
        Array.Copy(sourceWraps, _mainWrappedRowsWhileAlternate, Math.Min(sourceWraps.Length, newRows));
        if (_mainScrollbackWhileAlternate != null)
        {
            for (var index = 0; index < _mainScrollbackWhileAlternate.Count; index++)
            {
                var oldRow = _mainScrollbackWhileAlternate[index];
                var newRow = new TerminalCell[newColumns];
                for (var column = 0; column < newColumns; column++)
                    newRow[column] = CreateClearedCell();

                Array.Copy(oldRow, newRow, Math.Min(oldRow.Length, newColumns));
                _mainScrollbackWhileAlternate[index] = newRow;
            }
        }

        _mainScrollbackWrappedWhileAlternate ??= new List<bool>();
        var parkedScrollbackCount = _mainScrollbackWhileAlternate?.Count ?? 0;
        while (_mainScrollbackWrappedWhileAlternate.Count > parkedScrollbackCount)
            _mainScrollbackWrappedWhileAlternate.RemoveAt(_mainScrollbackWrappedWhileAlternate.Count - 1);

        _mainCursorRowWhileAlternate = Math.Clamp(_mainCursorRowWhileAlternate, 0, newRows - 1);
        _mainCursorColWhileAlternate = Math.Clamp(_mainCursorColWhileAlternate, 0, newColumns - 1);
    }

    private sealed class ReflowLine
    {
        public List<TerminalCell> Cells { get; } = new();
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
        _scrollbackWrapped.Clear();
        Array.Clear(_wrappedRows, 0, _wrappedRows.Length);
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);
    }

    public void ResetAttributes()
    {
        CurrentForeground = DefaultForegroundColor;
        CurrentBackground = DefaultBackgroundColor;
        CurrentBoldIntensity = false;
        CurrentBold = false;
        CurrentDim = false;
        CurrentItalic = false;
        CurrentUnderline = false;
        CurrentDoubleUnderline = false;
        CurrentBlinking = false;
        CurrentReverse = false;
        CurrentInvisible = false;
        CurrentStrikethrough = false;
    }

    public void ApplyBoldTextMode(string? mode)
    {
        UseBoldColor = !string.Equals(mode, "Font", StringComparison.OrdinalIgnoreCase);
        UseBoldFont = !string.Equals(mode, "Color", StringComparison.OrdinalIgnoreCase);
    }

    public void SetBoldIntensity(bool value)
    {
        CurrentBoldIntensity = value;
        CurrentBold = value && UseBoldFont;

        if (UseBoldColor)
            CurrentForeground = value ? BoldForegroundColor : DefaultForegroundColor;
    }

    public Color GetDefaultForegroundForCurrentIntensity()
    {
        return UseBoldColor && CurrentBoldIntensity ? BoldForegroundColor : DefaultForegroundColor;
    }

    public void ApplyColorScheme(Color defaultForeground, Color defaultBackground, Color boldForeground, Color[] ansiColors)
    {
        var oldDefaultForeground = DefaultForegroundColor;
        var oldDefaultBackground = DefaultBackgroundColor;
        var oldBoldForeground = BoldForegroundColor;
        var oldAnsiColors = AnsiColors.ToArray();

        DefaultForegroundColor = defaultForeground;
        DefaultBackgroundColor = defaultBackground;
        BoldForegroundColor = boldForeground;
        AnsiColors = ansiColors is { Length: >= 16 } ? ansiColors.Take(16).ToArray() : TerminalColors.Standard16.ToArray();

        CurrentForeground = MapColor(CurrentForeground, oldDefaultForeground, oldDefaultBackground, oldBoldForeground, oldAnsiColors);
        CurrentBackground = MapColor(CurrentBackground, oldDefaultForeground, oldDefaultBackground, oldBoldForeground, oldAnsiColors);

        for (int row = 0; row < Rows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                _cells[row, col].Foreground = MapColor(_cells[row, col].Foreground, oldDefaultForeground, oldDefaultBackground, oldBoldForeground, oldAnsiColors);
                _cells[row, col].Background = MapColor(_cells[row, col].Background, oldDefaultForeground, oldDefaultBackground, oldBoldForeground, oldAnsiColors);
            }
            DirtyRows.Add(row);
        }

        foreach (var scrollbackRow in _scrollback)
        {
            for (int col = 0; col < scrollbackRow.Length; col++)
            {
                scrollbackRow[col].Foreground = MapColor(scrollbackRow[col].Foreground, oldDefaultForeground, oldDefaultBackground, oldBoldForeground, oldAnsiColors);
                scrollbackRow[col].Background = MapColor(scrollbackRow[col].Background, oldDefaultForeground, oldDefaultBackground, oldBoldForeground, oldAnsiColors);
            }
        }

        RequestChange();
    }

    private Color MapColor(Color color, Color oldDefaultForeground, Color oldDefaultBackground, Color oldBoldForeground, Color[] oldAnsiColors)
    {
        if (color == oldDefaultForeground)
            return DefaultForegroundColor;
        if (color == oldDefaultBackground)
            return DefaultBackgroundColor;
        if (color == oldBoldForeground)
            return BoldForegroundColor;

        for (int i = 0; i < oldAnsiColors.Length && i < AnsiColors.Length; i++)
        {
            if (color == oldAnsiColors[i])
                return AnsiColors[i];
        }

        return color;
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
            Dim = false,
            Italic = false,
            Underline = false,
            DoubleUnderline = false,
            Blinking = false,
            Reverse = false,
            Invisible = false,
            Strikethrough = false,
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
            Dim = false,
            Italic = false,
            Underline = false,
            DoubleUnderline = false,
            Blinking = false,
            Reverse = false,
            Invisible = false,
            Strikethrough = false,
            IsWideContinuation = false
        };
    }

    public int ScrollbackCount => _scrollback.Count;

    public void MarkAllDirty()
    {
        for (int r = 0; r < Rows; r++)
            DirtyRows.Add(r);
    }

    private static bool[] BuildDefaultTabStops(int columns)
    {
        var tabs = new bool[Math.Max(1, columns)];
        for (var column = 8; column < tabs.Length; column += 8)
            tabs[column] = true;

        return tabs;
    }

    private static bool[] ResizeTabStops(bool[] old, int columns)
    {
        var tabs = BuildDefaultTabStops(columns);
        for (var column = 0; column < Math.Min(old.Length, tabs.Length); column++)
            tabs[column] = old[column];

        return tabs;
    }
}
