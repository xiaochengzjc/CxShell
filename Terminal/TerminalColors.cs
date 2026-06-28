using Avalonia.Media;

namespace CxShell.Terminal;

public static class TerminalColors
{
    public static readonly Color DefaultForeground = Color.Parse("#CCCCCC");
    public static readonly Color DefaultBackground = Color.Parse("#141414");

    // Standard 16 ANSI colors
    public static readonly Color[] Standard16 =
    {
        Color.Parse("#000000"), // 0 Black
        Color.Parse("#CC0000"), // 1 Red
        Color.Parse("#4E9A06"), // 2 Green
        Color.Parse("#C4A000"), // 3 Yellow
        Color.Parse("#3465A4"), // 4 Blue
        Color.Parse("#75507B"), // 5 Magenta
        Color.Parse("#06989A"), // 6 Cyan
        Color.Parse("#D3D7CF"), // 7 White
        Color.Parse("#555753"), // 8 Bright Black
        Color.Parse("#EF2929"), // 9 Bright Red
        Color.Parse("#8AE234"), // 10 Bright Green
        Color.Parse("#FCE94F"), // 11 Bright Yellow
        Color.Parse("#729FCF"), // 12 Bright Blue
        Color.Parse("#AD7FA8"), // 13 Bright Magenta
        Color.Parse("#34E2E2"), // 14 Bright Cyan
        Color.Parse("#EEEEEC"), // 15 Bright White
    };

    private static Color[]? _colors256;

    public static Color Get256Color(int index)
    {
        if (index < 16)
            return Standard16[index];

        _colors256 ??= Build256ColorTable();
        return _colors256[index];
    }

    private static Color[] Build256ColorTable()
    {
        var table = new Color[256];

        // First 16 are standard
        for (int i = 0; i < 16; i++)
            table[i] = Standard16[i];

        // 216 color cube (indices 16-231)
        int[] levels = { 0, 95, 135, 175, 215, 255 };
        for (int r = 0; r < 6; r++)
        for (int g = 0; g < 6; g++)
        for (int b = 0; b < 6; b++)
        {
            int index = 16 + 36 * r + 6 * g + b;
            table[index] = Color.FromRgb((byte)levels[r], (byte)levels[g], (byte)levels[b]);
        }

        // Grayscale (indices 232-255)
        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + 10 * i);
            table[232 + i] = Color.FromRgb(v, v, v);
        }

        return table;
    }
}
