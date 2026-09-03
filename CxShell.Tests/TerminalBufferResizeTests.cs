using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalBufferResizeTests
{
    [Fact]
    public void WideningRejoinsAutowrappedText()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 4, maxScrollback: 20);
        var parser = new AnsiParser(buffer);

        parser.Process("0123456789ABCDEF");
        buffer.Resize(16, 4);

        Assert.Equal("0123456789ABCDEF", buffer.ExportText());
        Assert.Equal("", buffer.GetCell(1, 0).GetText().Trim());
    }

    [Fact]
    public void NarrowingPreservesTheEntireAutowrappedLine()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 4, maxScrollback: 20);
        var parser = new AnsiParser(buffer);

        parser.Process("0123456789ABCDEFGHIJ");
        buffer.Resize(7, 4);

        var visible = buffer.ExportText();
        Assert.Contains("0123456", visible);
        Assert.Contains("789ABCD", visible);
        Assert.Contains("EFGHIJ", visible);
    }

    [Fact]
    public void AlternateScreenResizeDoesNotCreateScrollback()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 4, maxScrollback: 20);
        var parser = new AnsiParser(buffer);
        parser.Process("main");
        parser.Process("\x1b[?1049hPANEL VIEW");

        buffer.Resize(8, 4);

        Assert.True(buffer.IsAlternateScreen);
        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal("PANEL VI", string.Concat(
            Enumerable.Range(0, 8).Select(column => buffer.GetCell(0, column).GetText())));
    }
}
