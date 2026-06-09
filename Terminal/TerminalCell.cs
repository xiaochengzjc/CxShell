using Avalonia.Media;

namespace ChiXueSsh.Terminal;

public struct TerminalCell
{
    public char Character;
    public Color Foreground;
    public Color Background;
    public bool Bold;
    public bool Underline;

    public static TerminalCell Default => new()
    {
        Character = ' ',
        Foreground = TerminalColors.DefaultForeground,
        Background = TerminalColors.DefaultBackground,
        Bold = false,
        Underline = false
    };

    public void Reset()
    {
        Character = ' ';
        Foreground = TerminalColors.DefaultForeground;
        Background = TerminalColors.DefaultBackground;
        Bold = false;
        Underline = false;
    }
}
