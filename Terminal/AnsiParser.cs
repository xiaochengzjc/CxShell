using System;
using Avalonia.Media;

namespace ChiXueSsh.Terminal;

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
        OscString,
        OscEscape,
        CharsetDesignate
    }

    private State _state = State.Ground;
    private readonly int[] _params = new int[16];
    private int _paramCount;
    private int _currentParam;
    private char _intermediateChar;
    private char _charsetTarget;
    private bool _isPrivateMode; // ? prefix for private modes
    private bool _g0LineDrawing;
    private bool _g1LineDrawing;
    private bool _useG1;

    private readonly TerminalBuffer _buffer;

    public event Action? BellReceived;

    public AnsiParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Process(string data)
    {
        foreach (var ch in data)
        {
            ProcessChar(ch);
        }
    }

    private void ProcessChar(char ch)
    {
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
            case State.OscString:
                ProcessOscString(ch);
                break;
            case State.OscEscape:
                // After ESC in OSC, expect \ to terminate
                _state = State.Ground;
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
                    _buffer.PutChar(MapPrintableCharacter(ch));
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
                _state = State.OscString;
                break;
            case '(':
            case ')':
                _charsetTarget = ch;
                _state = State.CharsetDesignate;
                break;
            case 'M': // Reverse index
                if (_buffer.CursorRow == 0)
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
            _state = State.CsiParam;
        }
        else if (ch >= '0' && ch <= '9')
        {
            _currentParam = ch - '0';
            _state = State.CsiParam;
        }
        else if (ch == ';')
        {
            StoreParam();
            _state = State.CsiParam;
        }
        else if (ch >= 0x20 && ch <= 0x2F)
        {
            _intermediateChar = ch;
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
            _currentParam = _currentParam * 10 + (ch - '0');
        }
        else if (ch == ';')
        {
            StoreParam();
        }
        else if (ch >= 0x20 && ch <= 0x2F)
        {
            StoreParam();
            _intermediateChar = ch;
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
            _state = State.Ground;
        }
        // Otherwise consume characters
    }

    private void ExecuteCsi(char finalChar)
    {
        if (_isPrivateMode)
        {
            ExecutePrivateMode(finalChar);
            return;
        }

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
            case 'H': // CUP - Cursor Position
            case 'f':
                int row = GetParam(0, 1) - 1;
                int col = GetParam(1, 1) - 1;
                _buffer.MoveCursor(row, col);
                break;
            case 'J': // ED - Erase in Display
                int mode = GetParam(0, 0);
                switch (mode)
                {
                    case 0: // Clear to end
                        _buffer.ClearToEndOfScreen();
                        break;
                    case 1: // Clear to beginning
                        // Simplified: clear entire screen
                        _buffer.ClearScreen();
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
                _buffer.MoveCursor(GetParam(0, 1) - 1, _buffer.CursorCol);
                break;
            case 'G': // HPA - Horizontal Position Absolute
                _buffer.MoveCursor(_buffer.CursorRow, GetParam(0, 1) - 1);
                break;
            case 'L': // IL - Insert Lines
                // Simplified: scroll down from current line
                _buffer.ScrollDown();
                break;
            case 'M': // DL - Delete Lines
                _buffer.ScrollUp();
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
                // Ignored in basic implementation
                break;
            case 's': // SCP - Save cursor position
                break;
            case 'u': // RCP - Restore cursor position
                break;
            case 'n': // DSR - Device Status Report
                // Could report cursor position back
                break;
            case 'c': // DA - Device Attributes
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
        int mode = GetParam(0, 0);
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
                        _buffer.MoveCursor(0, 0);
                        break;
                    case 7:
                        _buffer.AutoWrapMode = true;
                        break;
                    case 25:
                        _buffer.CursorVisible = true;
                        break;
                    case 47:
                    case 1047:
                    case 1049:
                        if (!_buffer.DisableAlternateScreen)
                        {
                            _buffer.ClearScreen();
                            _buffer.MoveCursor(0, 0);
                        }
                        break;
                    case 66:
                        _buffer.NumericKeypadApplicationMode = true;
                        break;
                    case 5:
                        _buffer.ReverseVideoMode = true;
                        _buffer.MarkAllDirty();
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
                        _buffer.MoveCursor(0, 0);
                        break;
                    case 7:
                        _buffer.AutoWrapMode = false;
                        break;
                    case 25:
                        _buffer.CursorVisible = false;
                        break;
                    case 47:
                    case 1047:
                    case 1049:
                        if (!_buffer.DisableAlternateScreen)
                        {
                            _buffer.ClearScreen();
                            _buffer.MoveCursor(0, 0);
                        }
                        break;
                    case 66:
                        _buffer.NumericKeypadApplicationMode = false;
                        break;
                    case 5:
                        _buffer.ReverseVideoMode = false;
                        _buffer.MarkAllDirty();
                        break;
                }
                break;
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
                    _buffer.CurrentBold = true;
                    _buffer.CurrentForeground = _buffer.BoldForegroundColor;
                    break;
                case 4: // Underline
                    _buffer.CurrentUnderline = true;
                    break;
                case 5: // Blink
                    if (!_buffer.DisableBlinkingText)
                        _buffer.CurrentBlinking = true;
                    break;
                case 22: // Normal intensity
                    _buffer.CurrentBold = false;
                    _buffer.CurrentForeground = _buffer.DefaultForegroundColor;
                    break;
                case 24: // No underline
                    _buffer.CurrentUnderline = false;
                    break;
                case 25: // No blink
                    _buffer.CurrentBlinking = false;
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
                    _buffer.CurrentForeground = _buffer.CurrentBold ? _buffer.BoldForegroundColor : _buffer.DefaultForegroundColor;
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

    private void ResetParams()
    {
        _paramCount = 0;
        _currentParam = 0;
        _intermediateChar = '\0';
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

    private int GetParam(int index, int defaultValue)
    {
        if (index >= _paramCount) return defaultValue;
        int val = _params[index];
        return val == 0 ? defaultValue : val;
    }
}
