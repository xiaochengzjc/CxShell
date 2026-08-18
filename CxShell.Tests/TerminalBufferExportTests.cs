using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalBufferExportTests
{
    [Fact]
    public void ExportText_CombinesScrollbackAndCurrentScreenWithoutTrailingWhitespace()
    {
        var buffer = new TerminalBuffer(columns: 12, rows: 2, maxScrollback: 20);
        PutText(buffer, "first line");
        buffer.ClearScreen();
        buffer.MoveCursor(0, 0);
        PutText(buffer, "中文 second");

        Assert.Equal("first line\n中文 second", buffer.ExportText());
    }

    [Fact]
    public void ExportText_RemovesTrailingBlankRows()
    {
        var buffer = new TerminalBuffer(columns: 8, rows: 3);
        PutText(buffer, "line");

        Assert.Equal("line", buffer.ExportText());
    }

    private static void PutText(TerminalBuffer buffer, string text)
    {
        foreach (var character in text)
            buffer.PutChar(character);
    }
}
