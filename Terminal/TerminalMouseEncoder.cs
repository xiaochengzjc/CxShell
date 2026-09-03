using System.Text;

namespace CxShell.Terminal;

/// <summary>
/// 将终端控件中的指针事件编码为 X10、SGR 1006 或 urxvt 1015 鼠标报告。
/// </summary>
public static class TerminalMouseEncoder
{
    public static byte[]? Encode(
        TerminalMouseEventType type,
        TerminalMouseButton button,
        int column,
        int row,
        bool shift,
        bool alt,
        bool control,
        TerminalMouseTracking tracking,
        TerminalMouseEncoding encoding)
    {
        if (tracking == TerminalMouseTracking.None)
            return null;

        var isWheel = button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown;
        switch (tracking)
        {
            case TerminalMouseTracking.X10 when type != TerminalMouseEventType.Press || isWheel:
                return null;
            case TerminalMouseTracking.Normal when type == TerminalMouseEventType.Move:
                return null;
            case TerminalMouseTracking.Normal when type == TerminalMouseEventType.Release && isWheel:
                return null;
            case TerminalMouseTracking.ButtonEvent:
            case TerminalMouseTracking.AnyEvent:
                if (type == TerminalMouseEventType.Release && isWheel)
                    return null;
                break;
        }

        var code = isWheel
            ? (int)button
            : type == TerminalMouseEventType.Release && encoding != TerminalMouseEncoding.Sgr
                ? 3
                : (int)button;

        if (type == TerminalMouseEventType.Move)
            code += 32;

        if (tracking != TerminalMouseTracking.X10)
        {
            if (shift)
                code += 4;
            if (alt)
                code += 8;
            if (control)
                code += 16;
        }

        var x = Math.Max(1, column + 1);
        var y = Math.Max(1, row + 1);
        return encoding switch
        {
            TerminalMouseEncoding.Sgr => Ascii($"\x1b[<{code};{x};{y}{(type == TerminalMouseEventType.Release ? 'm' : 'M')}"),
            TerminalMouseEncoding.Urxvt => Ascii($"\x1b[{code + 32};{x};{y}M"),
            _ => EncodeX10(code, x, y)
        };
    }

    private static byte[] EncodeX10(int code, int column, int row)
    {
        return
        [
            0x1b, (byte)'[', (byte)'M',
            (byte)(32 + (code & 0xff)),
            (byte)(32 + Math.Clamp(column, 1, 223)),
            (byte)(32 + Math.Clamp(row, 1, 223))
        ];
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
}
