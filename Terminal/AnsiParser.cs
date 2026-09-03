using System;
using Avalonia.Media;
using System.Text;

namespace CxShell.Terminal;

/// <summary>
/// ANSI escape sequence parser - state machine implementation.
/// Handles SGR colors, cursor movement, screen clearing, and basic control characters.
/// </summary>
public class AnsiParser
{
    private enum State
    {
        Ground,
        Escape,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        EscapeIntermediate,
        OscString,
        OscEscape,
        CharsetDesignate,
        DcsString,
        DcsEscape,
        ControlString,
        ControlStringEscape
    }

    private State _state = State.Ground;
    private readonly int[] _params = new int[16];
    private int _paramCount;
    private int _currentParam;
    private char _intermediateChar;
    private char _charsetTarget;
    private bool _isPrivateMode; // ? prefix for private modes
    private char _csiPrefix;
    private bool _csiParameterStarted;
    private bool _g0LineDrawing;
    private bool _g1LineDrawing;
    private bool _useG1;
    private int _savedCursorRow;
    private int _savedCursorCol;
    private Color _savedForeground;
    private Color _savedBackground;
    private bool _savedBoldIntensity;
    private bool _savedBold;
    private bool _savedDim;
    private bool _savedItalic;
    private bool _savedUnderline;
    private bool _savedDoubleUnderline;
    private bool _savedBlinking;
    private bool _savedReverse;
    private bool _savedInvisible;
    private bool _savedStrikethrough;
    private bool _hasSavedCursor;
    private char _pendingHighSurrogate;
    private bool _csiHasSubparameters;
    private int _csiIntermediateCount;
    private readonly StringBuilder _oscBuffer = new();
    private readonly StringBuilder _dcsBuffer = new();

    private readonly TerminalBuffer _buffer;

    public event Action? BellReceived;
    public event Action<string>? OperatingSystemCommandReceived;
    public event Action<string>? DeviceControlCommandReceived;
    public event Action<char>? DeviceAttributesRequested;
    public event Action<int>? DeviceStatusReportRequested;
    public event Action<bool, int>? DeviceModeQueryRequested;
    public event Action? KittyKeyboardProtocolQueryRequested;

    public AnsiParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Drops a partially received escape/control string. A reconnect can begin
    /// with ordinary text, so parser state must never leak across sessions.
    /// </summary>
    public void ResetInputState()
    {
        _state = State.Ground;
        ResetParams();
        _charsetTarget = '\0';
        _isPrivateMode = false;
        _pendingHighSurrogate = '\0';
        _oscBuffer.Clear();
        _dcsBuffer.Clear();
        _g0LineDrawing = false;
        _g1LineDrawing = false;
        _useG1 = false;
        _hasSavedCursor = false;
        _buffer.ResetInputHyperlink();
    }

    public void Process(string data)
    {
        _buffer.BeginUpdate();
        try
        {
            foreach (var ch in data)
            {
                if (_pendingHighSurrogate != '\0')
                {
                    if (_state == State.Ground && char.IsLowSurrogate(ch))
                    {
                        _buffer.PutRune(char.ConvertToUtf32(_pendingHighSurrogate, ch));
                        _pendingHighSurrogate = '\0';
                        continue;
                    }

                    ProcessChar('\uFFFD');
                    _pendingHighSurrogate = '\0';
                }

                if (_state == State.Ground && char.IsHighSurrogate(ch))
                {
                    _pendingHighSurrogate = ch;
                    continue;
                }

                if (_state == State.Ground && char.IsLowSurrogate(ch))
                {
                    _buffer.PutRune(0xFFFD);
                    continue;
                }

                ProcessChar(ch);
            }
        }
        finally
        {
            _buffer.EndUpdate();
        }
    }

    private void ProcessChar(char ch)
    {
        if (ch is '\x18' or '\x1A')
        {
            _oscBuffer.Clear();
            _dcsBuffer.Clear();
            _state = State.Ground;
            ResetParams();
            return;
        }

        if (ch == '\x9C')
        {
            if (_state is State.OscString or State.OscEscape)
                CompleteOscString();
            else if (_state is State.DcsString or State.DcsEscape)
                CompleteDcsString();
            else if (_state is State.ControlString or State.ControlStringEscape)
                _state = State.Ground;
            return;
        }

        if (_state == State.Ground && ch is >= '\x80' and <= '\x9F')
        {
            ProcessC1Control(ch);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                ProcessGround(ch);
                break;
            case State.Escape:
                ProcessEscape(ch);
                break;
            case State.CsiEntry:
                ProcessCsiEntry(ch);
                break;
            case State.CsiParam:
                ProcessCsiParam(ch);
                break;
            case State.CsiIntermediate:
                ProcessCsiIntermediate(ch);
                break;
            case State.EscapeIntermediate:
                ProcessEscapeIntermediate(ch);
                break;
            case State.OscString:
                ProcessOscString(ch);
                break;
            case State.OscEscape:
                // After ESC in OSC, expect \ to terminate
                if (ch == '\\')
                    CompleteOscString();
                else
                    _state = State.Ground;
                break;
            case State.DcsString:
                ProcessDcsString(ch);
                break;
            case State.DcsEscape:
                if (ch == '\\')
                    CompleteDcsString();
                else
                {
                    if (_dcsBuffer.Length < 8192)
                        _dcsBuffer.Append('\x1B');
                    _state = State.DcsString;
                    ProcessDcsString(ch);
                }
                break;
            case State.ControlString:
                ProcessControlString(ch);
                break;
            case State.ControlStringEscape:
                if (ch == '\\')
                    _state = State.Ground;
                else
                    _state = State.ControlString;
                break;
            case State.CharsetDesignate:
                ProcessCharsetDesignation(ch);
                break;
        }
    }

    private void ProcessGround(char ch)
    {
        switch (ch)
        {
            case '\x1B': // ESC
                _state = State.Escape;
                break;
            case '\r': // CR
                _buffer.CarriageReturn();
                break;
            case '\n': // LF
                _buffer.LineFeed();
                break;
            case '\b': // BS
                _buffer.Backspace();
                break;
            case '\t': // TAB
                _buffer.Tab();
                break;
            case '\a': // BEL
                BellReceived?.Invoke();
                break;
            case '\x0E': // SO - shift out G1
                _useG1 = true;
                break;
            case '\x0F': // SI - shift in G0
                _useG1 = false;
                break;
            default:
                if (ch >= ' ')
                {
                    _buffer.PutRune(MapPrintableCharacter(ch));
                }
                break;
        }
    }

    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _state = State.CsiEntry;
                ResetParams();
                _isPrivateMode = false;
                break;
            case ']':
                _oscBuffer.Clear();
                _state = State.OscString;
                break;
            case 'P':
                _dcsBuffer.Clear();
                _state = State.DcsString;
                break;
            case '(':
            case ')':
                _charsetTarget = ch;
                _state = State.CharsetDesignate;
                break;
            case '#':
                _intermediateChar = '#';
                _state = State.EscapeIntermediate;
                break;
            case 'H': // HTS - set a tab stop at the current column
                _buffer.SetTabStop();
                _state = State.Ground;
                break;
            case 'M': // Reverse index
                if (_buffer.CursorRow == _buffer.ScrollTop)
                    _buffer.ScrollDown();
                else
                    _buffer.MoveCursorUp(1);
                _state = State.Ground;
                break;
            case 'D': // Index (line feed)
                _buffer.LineFeed();
                _state = State.Ground;
                break;
            case 'E': // Next line
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                _state = State.Ground;
                break;
            case '7': // DECSC - Save cursor
                SaveCursor();
                _state = State.Ground;
                break;
            case '8': // DECRC - Restore cursor
                RestoreCursor();
                _state = State.Ground;
                break;
            case 'c': // RIS - full terminal reset
                ExecuteFullReset();
                _state = State.Ground;
                break;
            default:
                _state = State.Ground;
                break;
        }
    }

    private void ProcessCsiEntry(char ch)
    {
        if (ch == '?')
        {
            _isPrivateMode = true;
            _csiPrefix = ch;
            _state = State.CsiParam;
        }
        else if (ch is '>' or '=' or '<')
        {
            _csiPrefix = ch;
            _state = State.CsiParam;
        }
        else if (ch >= '0' && ch <= '9')
        {
            _csiParameterStarted = true;
            _currentParam = ch - '0';
            _state = State.CsiParam;
        }
        else if (ch == ';')
        {
            _csiParameterStarted = true;
            StoreParam();
            _state = State.CsiParam;
        }
        else if (ch == ':')
        {
            _csiParameterStarted = true;
            _csiHasSubparameters = true;
            _state = State.CsiParam;
        }
        else if (ch >= 0x20 && ch <= 0x2F)
        {
            _intermediateChar = ch;
            _csiIntermediateCount++;
            _state = State.CsiIntermediate;
        }
        else if (ch >= 0x40 && ch <= 0x7E)
        {
            ExecuteCsi(ch);
            _state = State.Ground;
        }
        else
        {
            _state = State.Ground;
        }
    }

    private void ProcessCsiParam(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            _csiParameterStarted = true;
            AppendParamDigit(ch - '0');
        }
        else if (ch == ';')
        {
            _csiParameterStarted = true;
            StoreParam();
        }
        else if (ch == ':')
        {
            _csiParameterStarted = true;
            // Colon subparameters are intentionally consumed as one opaque
            // parameter group until their final byte. This keeps unsupported
            // modern sequences from leaking their payload onto the screen.
            _csiHasSubparameters = true;
        }
        else if (ch >= 0x20 && ch <= 0x2F)
        {
            StoreParam();
            _intermediateChar = ch;
            _csiIntermediateCount++;
            _state = State.CsiIntermediate;
        }
        else if (ch >= 0x40 && ch <= 0x7E)
        {
            StoreParam();
            ExecuteCsi(ch);
            _state = State.Ground;
        }
        else
        {
            _state = State.Ground;
        }
    }

    private void ProcessCsiIntermediate(char ch)
    {
        if (ch >= 0x20 && ch <= 0x2F)
        {
            _intermediateChar = ch;
            _csiIntermediateCount++;
        }
        else if (ch >= 0x40 && ch <= 0x7E)
        {
            ExecuteCsi(ch);
            _state = State.Ground;
        }
        else
        {
            _state = State.Ground;
        }
    }

    private void ProcessOscString(char ch)
    {
        if (ch == '\x1B')
        {
            _state = State.OscEscape;
        }
        else if (ch == '\a') // BEL terminates OSC
        {
            CompleteOscString();
        }
        else if (_oscBuffer.Length < 8192)
        {
            _oscBuffer.Append(ch);
        }
        // Otherwise consume characters.
    }

    private void CompleteOscString()
    {
        var command = _oscBuffer.ToString();
        _oscBuffer.Clear();
        _state = State.Ground;
        if (TryApplyHyperlinkCommand(command))
            return;

        if (!string.IsNullOrEmpty(command))
            OperatingSystemCommandReceived?.Invoke(command);
    }

    private bool TryApplyHyperlinkCommand(string command)
    {
        if (!command.StartsWith("8;", StringComparison.Ordinal))
            return false;

        var separator = command.IndexOf(';', 2);
        if (separator < 0)
            return true;

        var uri = command[(separator + 1)..];
        if (uri.Length > 4096)
            return true;

        foreach (var character in uri)
        {
            if (character < ' ' || character == '\x7F')
                return true;
        }

        _buffer.SetHyperlink(uri);
        return true;
    }

    private void ProcessDcsString(char ch)
    {
        if (ch == '\x1B')
        {
            _state = State.DcsEscape;
            return;
        }

        if (ch == '\a')
        {
            CompleteDcsString();
            return;
        }

        if (_dcsBuffer.Length < 8192)
            _dcsBuffer.Append(ch);
    }

    private void CompleteDcsString()
    {
        var command = _dcsBuffer.ToString();
        _dcsBuffer.Clear();
        _state = State.Ground;
        if (!string.IsNullOrEmpty(command))
            DeviceControlCommandReceived?.Invoke(command);
    }

    private void ExecuteCsi(char finalChar)
    {
        if (_intermediateChar == '$' && finalChar == 'p')
        {
            if (_paramCount > 0)
                DeviceModeQueryRequested?.Invoke(_isPrivateMode, _params[0]);
            return;
        }

        if (_csiPrefix == '?' && finalChar == 'u' && !_csiParameterStarted)
        {
            KittyKeyboardProtocolQueryRequested?.Invoke();
            return;
        }

        if (finalChar == 'u' && _csiPrefix is ('=' or '>' or '<'))
        {
            ExecuteKittyKeyboardCommand();
            return;
        }

        if (_isPrivateMode)
        {
            ExecutePrivateMode(finalChar);
            return;
        }

        if (_intermediateChar == ' ' && finalChar == 'q')
        {
            _buffer.SetCursorStyle(GetParam(0, 0));
            return;
        }

        if (_csiHasSubparameters)
            return;

        if (_csiPrefix == '>' && finalChar == 'm')
        {
            ExecuteModifyOtherKeys();
            return;
        }

        if (_intermediateChar == '!' && finalChar == 'p')
        {
            ExecuteSoftReset();
            return;
        }

        if (_csiIntermediateCount > 1)
            return;

        // CSI intermediates define a different command grammar. Unknown
        // intermediate sequences must be consumed without running a command
        // that happens to share the same final byte.
        if (_intermediateChar != '\0')
            return;

        // CSI >/=/< has its own grammar. Unsupported prefixed commands must be
        // consumed silently instead of falling through to an unprefixed command
        // with the same final byte (notably vim's modifyOtherKeys probe).
        if ((_csiPrefix is '>' or '=' or '<') && finalChar != 'c')
            return;

        switch (finalChar)
        {
            case 'A': // CUU - Cursor Up
                _buffer.MoveCursorUp(GetParam(0, 1));
                break;
            case 'B': // CUD - Cursor Down
                _buffer.MoveCursorDown(GetParam(0, 1));
                break;
            case 'C': // CUF - Cursor Forward
                _buffer.MoveCursorForward(GetParam(0, 1));
                break;
            case 'D': // CUB - Cursor Back
                _buffer.MoveCursorBack(GetParam(0, 1));
                break;
            case 'E': // CNL - Cursor Next Line
                _buffer.MoveCursorDown(GetParam(0, 1));
                _buffer.CarriageReturn();
                break;
            case 'F': // CPL - Cursor Previous Line
                _buffer.MoveCursorUp(GetParam(0, 1));
                _buffer.CarriageReturn();
                break;
            case 'H': // CUP - Cursor Position
            case 'f':
                int row = GetParam(0, 1) - 1;
                int col = GetParam(1, 1) - 1;
                _buffer.MoveCursorPosition(row, col);
                break;
            case 'I': // CHT - Cursor Horizontal Tabulation
                _buffer.TabForward(GetParam(0, 1));
                break;
            case 'Z': // CBT - Cursor Backward Tabulation
                _buffer.TabBackward(GetParam(0, 1));
                break;
            case 'a': // HPR - Horizontal Position Relative
                _buffer.MoveCursorForward(GetParam(0, 1));
                break;
            case 'J': // ED - Erase in Display
                int mode = GetParam(0, 0);
                switch (mode)
                {
                    case 0: // Clear to end
                        _buffer.ClearToEndOfScreen();
                        break;
                    case 1: // Clear to beginning
                        _buffer.ClearToBeginningOfScreen();
                        break;
                    case 2: // Clear entire screen
                        _buffer.ClearScreen();
                        _buffer.MoveCursor(0, 0);
                        break;
                    case 3: // Clear entire screen + scrollback
                        _buffer.ClearScreen(clearScrollback: true);
                        _buffer.MoveCursor(0, 0);
                        break;
                }
                break;
            case 'K': // EL - Erase in Line
                int lineMode = GetParam(0, 0);
                switch (lineMode)
                {
                    case 0: // Clear to end of line
                        _buffer.ClearToEndOfLine();
                        break;
                    case 1: // Clear to beginning of line
                        _buffer.ClearToBeginningOfLine();
                        break;
                    case 2: // Clear entire line
                        _buffer.ClearLine();
                        break;
                }
                break;
            case 'm': // SGR - Select Graphic Rendition
                ExecuteSgr();
                break;
            case 'S': // SU - Scroll Up
                for (int i = 0; i < GetParam(0, 1); i++)
                    _buffer.ScrollUp();
                break;
            case 'T': // SD - Scroll Down
                for (int i = 0; i < GetParam(0, 1); i++)
                    _buffer.ScrollDown();
                break;
            case 'd': // VPA - Vertical Position Absolute
                _buffer.MoveCursorVerticalAbsolute(GetParam(0, 1) - 1);
                break;
            case 'e': // VPR - Vertical Position Relative
                _buffer.MoveCursorDown(GetParam(0, 1));
                break;
            case 'G': // HPA - Horizontal Position Absolute
                _buffer.MoveCursor(_buffer.CursorRow, GetParam(0, 1) - 1);
                break;
            case 'L': // IL - Insert Lines
                _buffer.InsertLines(GetParam(0, 1));
                break;
            case 'M': // DL - Delete Lines
                _buffer.DeleteLines(GetParam(0, 1));
                break;
            case 'P': // DCH - Delete Characters
                _buffer.DeleteCharacters(GetParam(0, 1));
                break;
            case '@': // ICH - Insert blank characters
                _buffer.InsertBlankCharacters(GetParam(0, 1));
                break;
            case 'X': // ECH - Erase characters
                _buffer.EraseCharacters(GetParam(0, 1));
                break;
            case 'r': // DECSTBM - Set scrolling region
                var top = GetParam(0, 1) - 1;
                var bottom = GetParam(1, _buffer.Rows) - 1;
                _buffer.SetScrollRegion(top, bottom);
                break;
            case 'g': // TBC - Tabulation Clear
                switch (GetParam(0, 0))
                {
                    case 0:
                        _buffer.ClearTabStopAtCursor();
                        break;
                    case 3:
                        _buffer.ClearAllTabStops();
                        break;
                }
                break;
            case 's': // SCP - Save cursor position
                SaveCursor();
                break;
            case 'u': // RCP - Restore cursor position
                RestoreCursor();
                break;
            case 'n': // DSR - Device Status Report
                // The view model owns the connection, so response-capable reports
                // are surfaced there instead of making the parser depend on SSH.
                DeviceStatusReportRequested?.Invoke(GetParam(0, 0));
                break;
            case 'c': // DA - Device Attributes
                DeviceAttributesRequested?.Invoke(_csiPrefix);
                break;
            case 'h': // SM - Set Mode
                ExecuteSetMode(enabled: true);
                break;
            case 'l': // RM - Reset Mode
                ExecuteSetMode(enabled: false);
                break;
            case 't': // Window manipulation
                break;
        }
    }

    private void ProcessC1Control(char ch)
    {
        switch (ch)
        {
            case '\x84': // IND
                _buffer.LineFeed();
                break;
            case '\x85': // NEL
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                break;
            case '\x88': // HTS
                _buffer.SetTabStop();
                break;
            case '\x8D': // RI
                if (_buffer.CursorRow == _buffer.ScrollTop)
                    _buffer.ScrollDown();
                else
                    _buffer.MoveCursorUp(1);
                break;
            case '\x90': // DCS
                _dcsBuffer.Clear();
                _state = State.DcsString;
                break;
            case '\x9B': // CSI
                ResetParams();
                _isPrivateMode = false;
                _state = State.CsiEntry;
                break;
            case '\x9D': // OSC
                _oscBuffer.Clear();
                _state = State.OscString;
                break;
            case '\x98': // SOS
            case '\x9E': // PM
            case '\x9F': // APC
                _state = State.ControlString;
                break;
        }
    }

    private void ProcessControlString(char ch)
    {
        if (ch == '\x1B')
            _state = State.ControlStringEscape;
    }

    private void ProcessEscapeIntermediate(char ch)
    {
        if (_intermediateChar == '#' && ch == '8')
            _buffer.FillScreenWithCharacter('E');

        _intermediateChar = '\0';
        _state = State.Ground;
    }

    private void SaveCursor()
    {
        _savedCursorRow = _buffer.CursorRow;
        _savedCursorCol = _buffer.CursorCol;
        _savedForeground = _buffer.CurrentForeground;
        _savedBackground = _buffer.CurrentBackground;
        _savedBoldIntensity = _buffer.CurrentBoldIntensity;
        _savedBold = _buffer.CurrentBold;
        _savedDim = _buffer.CurrentDim;
        _savedItalic = _buffer.CurrentItalic;
        _savedUnderline = _buffer.CurrentUnderline;
        _savedDoubleUnderline = _buffer.CurrentDoubleUnderline;
        _savedBlinking = _buffer.CurrentBlinking;
        _savedReverse = _buffer.CurrentReverse;
        _savedInvisible = _buffer.CurrentInvisible;
        _savedStrikethrough = _buffer.CurrentStrikethrough;
        _hasSavedCursor = true;
    }

    private void RestoreCursor()
    {
        if (!_hasSavedCursor)
            return;

        _buffer.CurrentForeground = _savedForeground;
        _buffer.CurrentBackground = _savedBackground;
        _buffer.CurrentBoldIntensity = _savedBoldIntensity;
        _buffer.CurrentBold = _savedBold;
        _buffer.CurrentDim = _savedDim;
        _buffer.CurrentItalic = _savedItalic;
        _buffer.CurrentUnderline = _savedUnderline;
        _buffer.CurrentDoubleUnderline = _savedDoubleUnderline;
        _buffer.CurrentBlinking = _savedBlinking;
        _buffer.CurrentReverse = _savedReverse;
        _buffer.CurrentInvisible = _savedInvisible;
        _buffer.CurrentStrikethrough = _savedStrikethrough;
        _buffer.MoveCursor(_savedCursorRow, _savedCursorCol);
    }

    private void ProcessCharsetDesignation(char ch)
    {
        var lineDrawing = ch == '0';
        if (_charsetTarget == '(')
            _g0LineDrawing = lineDrawing;
        else if (_charsetTarget == ')')
            _g1LineDrawing = lineDrawing;

        _charsetTarget = '\0';
        _state = State.Ground;
    }

    private char MapPrintableCharacter(char ch)
    {
        if (!_buffer.UseBuiltinLineDrawing)
            return ch;

        var lineDrawingActive = _useG1 ? _g1LineDrawing : _g0LineDrawing;
        if (!lineDrawingActive)
            return ch;

        return ch switch
        {
            '`' => '◆',
            'a' => '▒',
            'f' => '°',
            'g' => '±',
            'j' => '┘',
            'k' => '┐',
            'l' => '┌',
            'm' => '└',
            'n' => '┼',
            'q' => '─',
            't' => '├',
            'u' => '┤',
            'v' => '┴',
            'w' => '┬',
            'x' => '│',
            'y' => '≤',
            'z' => '≥',
            '{' => 'π',
            '|' => '≠',
            '}' => '£',
            '~' => '·',
            _ => ch
        };
    }

    private void ExecuteSetMode(bool enabled)
    {
        int mode = GetParam(0, 0);
        switch (mode)
        {
            case 4: // IRM
                _buffer.InsertMode = enabled;
                break;
            case 12: // SRM: reset means local echo in many terminals; expose as direct flag.
                break;
            case 20: // LNM
                _buffer.NewLineMode = enabled;
                break;
        }
    }

    private void ExecutePrivateMode(char finalChar)
    {
        for (var index = 0; index < _paramCount; index++)
        {
            int mode = _params[index];
            switch (finalChar)
            {
                case 'h': // DECSET
                    switch (mode)
                    {
                        case 1:
                            _buffer.CursorKeyApplicationMode = true;
                            break;
                        case 6:
                            _buffer.OriginMode = true;
                            _buffer.MoveCursorHome();
                            break;
                        case 7:
                            _buffer.AutoWrapMode = true;
                            break;
                        case 25:
                            _buffer.CursorVisible = true;
                            break;
                        case 47:
                        case 1047:
                            _buffer.EnterAlternateScreen();
                            break;
                        case 1048:
                            SaveCursor();
                            break;
                        case 1049:
                            SaveCursor();
                            _buffer.EnterAlternateScreen();
                            break;
                        case 66:
                            _buffer.NumericKeypadApplicationMode = true;
                            break;
                        case 5:
                            _buffer.ReverseVideoMode = true;
                            _buffer.MarkAllDirty();
                            break;
                        case 9:
                            _buffer.MouseTracking = TerminalMouseTracking.X10;
                            break;
                        case 1000:
                            _buffer.MouseTracking = TerminalMouseTracking.Normal;
                            break;
                        case 1002:
                            _buffer.MouseTracking = TerminalMouseTracking.ButtonEvent;
                            break;
                        case 1003:
                            _buffer.MouseTracking = TerminalMouseTracking.AnyEvent;
                            break;
                        case 1006:
                            _buffer.MouseEncoding = TerminalMouseEncoding.Sgr;
                            break;
                        case 1015:
                            _buffer.MouseEncoding = TerminalMouseEncoding.Urxvt;
                            break;
                        case 2004:
                            _buffer.BracketedPasteMode = true;
                            break;
                        case 1004:
                            _buffer.FocusReportingMode = true;
                            break;
                        case 2026:
                            _buffer.SetSynchronizedOutputMode(true);
                            break;
                    }
                    break;
                case 'l': // DECRST
                    switch (mode)
                    {
                        case 1:
                            _buffer.CursorKeyApplicationMode = false;
                            break;
                        case 6:
                            _buffer.OriginMode = false;
                            _buffer.MoveCursorHome();
                            break;
                        case 7:
                            _buffer.AutoWrapMode = false;
                            break;
                        case 25:
                            _buffer.CursorVisible = false;
                            break;
                        case 47:
                        case 1047:
                            _buffer.ExitAlternateScreen();
                            break;
                        case 1048:
                            RestoreCursor();
                            break;
                        case 1049:
                            _buffer.ExitAlternateScreen();
                            RestoreCursor();
                            break;
                        case 66:
                            _buffer.NumericKeypadApplicationMode = false;
                            break;
                        case 5:
                            _buffer.ReverseVideoMode = false;
                            _buffer.MarkAllDirty();
                            break;
                        case 9:
                        case 1000:
                        case 1002:
                        case 1003:
                            _buffer.MouseTracking = TerminalMouseTracking.None;
                            break;
                        case 1006:
                            if (_buffer.MouseEncoding == TerminalMouseEncoding.Sgr)
                                _buffer.MouseEncoding = TerminalMouseEncoding.Default;
                            break;
                        case 1015:
                            if (_buffer.MouseEncoding == TerminalMouseEncoding.Urxvt)
                                _buffer.MouseEncoding = TerminalMouseEncoding.Default;
                            break;
                        case 2004:
                            _buffer.BracketedPasteMode = false;
                            break;
                        case 1004:
                            _buffer.FocusReportingMode = false;
                            break;
                        case 2026:
                            _buffer.SetSynchronizedOutputMode(false);
                            break;
                    }
                    break;
            }
        }
    }

    private void ExecuteSgr()
    {
        if (_paramCount == 0)
        {
            _buffer.ResetAttributes();
            return;
        }

        for (int i = 0; i < _paramCount; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: // Reset
                    _buffer.ResetAttributes();
                    break;
                case 1: // Bold
                    _buffer.SetBoldIntensity(true);
                    break;
                case 2: // Dim/faint
                    _buffer.CurrentDim = true;
                    break;
                case 3: // Italic
                    _buffer.CurrentItalic = true;
                    break;
                case 4: // Underline
                    _buffer.CurrentUnderline = true;
                    _buffer.CurrentDoubleUnderline = false;
                    break;
                case 8: // Conceal/invisible
                    _buffer.CurrentInvisible = true;
                    break;
                case 5: // Blink
                    if (!_buffer.DisableBlinkingText)
                        _buffer.CurrentBlinking = true;
                    break;
                case 22: // Normal intensity
                    _buffer.SetBoldIntensity(false);
                    _buffer.CurrentDim = false;
                    break;
                case 23: // Not italic
                    _buffer.CurrentItalic = false;
                    break;
                case 24: // No underline
                    _buffer.CurrentUnderline = false;
                    _buffer.CurrentDoubleUnderline = false;
                    break;
                case 25: // No blink
                    _buffer.CurrentBlinking = false;
                    break;
                case 7: // Reverse video
                    _buffer.CurrentReverse = true;
                    break;
                case 9: // Strikethrough
                    _buffer.CurrentStrikethrough = true;
                    break;
                case 27: // Not reverse video
                    _buffer.CurrentReverse = false;
                    break;
                case 29: // Not strikethrough
                    _buffer.CurrentStrikethrough = false;
                    break;
                case 21: // Double underline
                    _buffer.CurrentDoubleUnderline = true;
                    _buffer.CurrentUnderline = false;
                    break;
                case 28: // Reveal
                    _buffer.CurrentInvisible = false;
                    break;
                case >= 30 and <= 37: // Standard foreground
                    _buffer.CurrentForeground = _buffer.GetAnsiColor(p - 30);
                    break;
                case 38: // Extended foreground
                    if (i + 1 < _paramCount)
                    {
                        if (_params[i + 1] == 5 && i + 2 < _paramCount)
                        {
                            // 256 color
                            _buffer.CurrentForeground = TerminalColors.Get256Color(_params[i + 2]);
                            i += 2;
                        }
                        else if (_params[i + 1] == 2 && i + 4 < _paramCount)
                        {
                            // True color (RGB)
                            _buffer.CurrentForeground = Color.FromRgb(
                                (byte)_params[i + 2],
                                (byte)_params[i + 3],
                                (byte)_params[i + 4]);
                            i += 4;
                        }
                    }
                    break;
                case 39: // Default foreground
                    _buffer.CurrentForeground = _buffer.GetDefaultForegroundForCurrentIntensity();
                    break;
                case >= 40 and <= 47: // Standard background
                    _buffer.CurrentBackground = _buffer.GetAnsiColor(p - 40);
                    break;
                case 48: // Extended background
                    if (i + 1 < _paramCount)
                    {
                        if (_params[i + 1] == 5 && i + 2 < _paramCount)
                        {
                            _buffer.CurrentBackground = TerminalColors.Get256Color(_params[i + 2]);
                            i += 2;
                        }
                        else if (_params[i + 1] == 2 && i + 4 < _paramCount)
                        {
                            _buffer.CurrentBackground = Color.FromRgb(
                                (byte)_params[i + 2],
                                (byte)_params[i + 3],
                                (byte)_params[i + 4]);
                            i += 4;
                        }
                    }
                    break;
                case 49: // Default background
                    _buffer.CurrentBackground = _buffer.DefaultBackgroundColor;
                    break;
                case >= 90 and <= 97: // Bright foreground
                    _buffer.CurrentForeground = _buffer.GetAnsiColor(p - 90 + 8);
                    break;
                case >= 100 and <= 107: // Bright background
                    _buffer.CurrentBackground = _buffer.GetAnsiColor(p - 100 + 8);
                    break;
            }
        }
    }

    private void ExecuteModifyOtherKeys()
    {
        if (_paramCount == 0 || _params[0] != 4)
            return;

        var level = _paramCount > 1 ? _params[1] : 1;
        _buffer.ModifyOtherKeysMode = Math.Clamp(level, 0, 2);
    }

    private void ExecuteSoftReset()
    {
        _buffer.AutoWrapMode = true;
        _buffer.OriginMode = false;
        _buffer.ReverseVideoMode = false;
        _buffer.NewLineMode = false;
        _buffer.InsertMode = false;
        _buffer.CursorKeyApplicationMode = false;
        _buffer.NumericKeypadApplicationMode = false;
        _buffer.BracketedPasteMode = false;
        _buffer.FocusReportingMode = false;
        _buffer.ModifyOtherKeysMode = 0;
        _buffer.ResetKittyKeyboardFlags();
        _buffer.SetSynchronizedOutputMode(false);
        _buffer.CursorVisible = true;
        _buffer.MouseTracking = TerminalMouseTracking.None;
        _buffer.MouseEncoding = TerminalMouseEncoding.Default;
        _buffer.ResetScrollRegion();
        _buffer.ResetAttributes();
        _buffer.ResetInputHyperlink();
        _buffer.ResetCursorStyle();
        _g0LineDrawing = false;
        _g1LineDrawing = false;
        _useG1 = false;
        _buffer.MarkAllDirty();
    }

    private void ExecuteFullReset()
    {
        ExecuteSoftReset();
        _buffer.ResetTabStops();
        _buffer.ClearScreen(clearScrollback: true);
        _buffer.MoveCursorHome();
        _savedCursorRow = 0;
        _savedCursorCol = 0;
        _hasSavedCursor = false;
    }

    private void ResetParams()
    {
        _paramCount = 0;
        _currentParam = 0;
        _intermediateChar = '\0';
        _csiPrefix = '\0';
        _csiParameterStarted = false;
        _csiHasSubparameters = false;
        _csiIntermediateCount = 0;
        for (int i = 0; i < _params.Length; i++)
            _params[i] = 0;
    }

    private void StoreParam()
    {
        if (_paramCount < _params.Length)
        {
            _params[_paramCount++] = _currentParam;
            _currentParam = 0;
        }
    }

    private void AppendParamDigit(int digit)
    {
        const int maxParameter = 1_000_000_000;
        _currentParam = _currentParam > (maxParameter - digit) / 10
            ? maxParameter
            : _currentParam * 10 + digit;
    }

    private int GetParam(int index, int defaultValue)
    {
        if (index >= _paramCount) return defaultValue;
        int val = _params[index];
        return val == 0 ? defaultValue : val;
    }

    private void ExecuteKittyKeyboardCommand()
    {
        switch (_csiPrefix)
        {
            case '=':
                // CSI = flags ; mode u. Mode 1 replaces, mode 2 sets, and
                // mode 3 clears the requested bits.
                _buffer.ApplyKittyKeyboardFlags(
                    _paramCount > 0 ? _params[0] : 0,
                    GetParam(1, 1));
                break;
            case '>':
                // CSI > flags u pushes the current flags and installs flags.
                _buffer.PushKittyKeyboardFlags(_paramCount > 0 ? _params[0] : 0);
                break;
            case '<':
                // CSI < number u pops number entries. An empty stack resets
                // the flags, as required by the Kitty protocol.
                _buffer.PopKittyKeyboardFlags(GetParam(0, 1));
                break;
        }
    }
}
