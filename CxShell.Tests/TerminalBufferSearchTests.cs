using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalBufferSearchTests
{
    [Fact]
    public void FindTextMatches_IsCaseInsensitiveAcrossScrollbackAndScreen()
    {
        var buffer = new TerminalBuffer(columns: 24, rows: 2, maxScrollback: 20);
        PutText(buffer, "Error: disk full");
        buffer.ClearScreen();
        buffer.MoveCursor(0, 0);
        PutText(buffer, "error: retrying");

        var matches = buffer.FindTextMatches("error");

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Row);
        Assert.Equal(1, matches[1].Row);
    }

    [Fact]
    public void FindTextMatches_RejectsEmptyQuery()
    {
        var buffer = new TerminalBuffer();
        PutText(buffer, "output");

        Assert.Empty(buffer.FindTextMatches(string.Empty));
    }

    private static void PutText(TerminalBuffer buffer, string text)
    {
        foreach (var character in text)
            buffer.PutChar(character);
    }
}
