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

    [Fact]
    public void HighVolumeOutput_BoundsScrollbackAndKeepsLatestSearchable()
    {
        const int lineCount = 25_000;
        const int maxScrollback = 5_000;
        var buffer = new TerminalBuffer(columns: 120, rows: 40, maxScrollback: maxScrollback);

        for (var line = 0; line < lineCount; line++)
        {
            PutText(buffer, $"line-{line:D5} cpu={line % 100:D2}% memory={line % 80:D2}%");
            buffer.CarriageReturn();
            buffer.LineFeed();
        }

        Assert.Equal(maxScrollback, buffer.ScrollbackCount);
        Assert.Contains(buffer.FindTextMatches($"line-{lineCount - 1:D5}"),
            match => match.Row >= buffer.ScrollbackCount);
        Assert.Empty(buffer.FindTextMatches("line-00000"));

        var oldestVisible = buffer.GetViewportCell(0, 0, buffer.MaxMeaningfulScrollOffset);
        var newestVisible = buffer.GetViewportCell(0, 0, 0);
        Assert.NotEqual('\0', oldestVisible.Character);
        Assert.NotEqual('\0', newestVisible.Character);
    }

    private static void PutText(TerminalBuffer buffer, string text)
    {
        foreach (var character in text)
            buffer.PutChar(character);
    }
}
