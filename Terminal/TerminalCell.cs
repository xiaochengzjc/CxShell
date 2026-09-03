using Avalonia.Media;

namespace CxShell.Terminal;

public struct TerminalCell
{
    public char Character;
    /// <summary>
    /// Complete text for a supplementary-plane character or a base character
    /// followed by combining marks. Common BMP cells keep this null.
    /// </summary>
    public string? Text;
    public Color Foreground;
    public Color Background;
    public bool Bold;
    public bool Dim;
    public bool Italic;
    public bool Underline;
    public bool DoubleUnderline;
    public bool Blinking;
    public bool Reverse;
    public bool Invisible;
    public bool Strikethrough;
    /// <summary>OSC 8 hyperlink associated with this cell, when present.</summary>
    public string? HyperlinkUri;
    public bool IsWideContinuation;

    public readonly string GetText()
    {
        if (Text != null)
            return Text;

        return Character == '\0' ? " " : Character.ToString();
    }

    public static TerminalCell Default => new()
    {
        Character = ' ',
        Foreground = TerminalColors.DefaultForeground,
        Background = TerminalColors.DefaultBackground,
        Bold = false,
        Dim = false,
        Italic = false,
        Underline = false,
        DoubleUnderline = false,
        Blinking = false,
        Reverse = false,
        Invisible = false,
        Strikethrough = false,
        HyperlinkUri = null,
        IsWideContinuation = false
    };

    public void Reset()
    {
        Character = ' ';
        Text = null;
        Foreground = TerminalColors.DefaultForeground;
        Background = TerminalColors.DefaultBackground;
        Bold = false;
        Dim = false;
        Italic = false;
        Underline = false;
        DoubleUnderline = false;
        Blinking = false;
        Reverse = false;
        Invisible = false;
        Strikethrough = false;
        IsWideContinuation = false;
    }
}
