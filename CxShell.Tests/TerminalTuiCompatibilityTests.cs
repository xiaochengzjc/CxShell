using CxShell.Terminal;

namespace CxShell.Tests;

/// <summary>
/// Representative control streams emitted by common full-screen terminal
/// programs. These are protocol-level tests, so they run on every platform
/// without requiring an interactive SSH server.
/// </summary>
public sealed class TerminalTuiCompatibilityTests
{
    [Theory]
    [InlineData("nano", "\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H\x1b[7m GNU nano 8.0 \x1b[0m\x1b[?25h")]
    [InlineData("vim", "\x1b[?1049h\x1b[?1h\x1b=\x1b[?25l\x1b[2J\x1b[1;1H~\x1b[?25h")]
    [InlineData("top", "\x1b[?1049h\x1b[?1h\x1b=\x1b[?25l\x1b[2J\x1b[Htop - 12:00:00\x1b[?25h")]
    [InlineData("less", "\x1b[?1049h\x1b[?25l\x1b[2J\x1b[Hfile.txt\x1b[?25h")]
    [InlineData("mc", "\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[?25l\x1b[2J\x1b[HFile\x1b[?25h")]
    public void CommonTuiStreamsRenderWithoutLeakingControlText(string program, string stream)
    {
        var buffer = new TerminalBuffer(columns: 80, rows: 24, maxScrollback: 100);
        var parser = new AnsiParser(buffer);

        parser.Process(stream);

        Assert.True(buffer.IsAlternateScreen, $"{program} should use the alternate screen");
        Assert.DoesNotContain("1049", buffer.ExportText());
        Assert.DoesNotContain("?25", buffer.ExportText());
        Assert.NotEqual('\0', buffer.GetCell(0, 0).Character);
    }

    [Fact]
    public void TuiScrollRegionAndCursorAddressingStayInsideTheRegion()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 8);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[2;7r\x1b[?6h\x1b[1;1Hheader\x1b[6;1Hfooter\x1b[2;1Hrow");

        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(3, buffer.CursorCol);
        Assert.Equal('h', buffer.GetCell(1, 0).Character);
        Assert.Equal('r', buffer.GetCell(2, 0).Character);
    }

    [Fact]
    public void TuiMouseAndBracketedPasteModesCanBeTurnedOnAndOffTogether()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[?1002;1006;2004h");
        Assert.Equal(TerminalMouseTracking.ButtonEvent, buffer.MouseTracking);
        Assert.Equal(TerminalMouseEncoding.Sgr, buffer.MouseEncoding);
        Assert.True(buffer.BracketedPasteMode);

        parser.Process("\x1b[?1002;1006;2004l");
        Assert.Equal(TerminalMouseTracking.None, buffer.MouseTracking);
        Assert.Equal(TerminalMouseEncoding.Default, buffer.MouseEncoding);
        Assert.False(buffer.BracketedPasteMode);
    }

    [Fact]
    public void VimStyleCapabilityQueriesProduceEventsWithoutVisibleText()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);
        var modeQuery = (-1, -1);
        string? dcs = null;
        parser.DeviceModeQueryRequested += (isPrivate, mode) => modeQuery = (isPrivate ? 1 : 0, mode);
        parser.DeviceControlCommandReceived += command => dcs = command;

        parser.Process("\x1b[?1004$p\x1bP$qm\x1b\\");

        Assert.Equal((1, 1004), modeQuery);
        Assert.Equal("$qm", dcs);
        Assert.DoesNotContain("1004", buffer.ExportText());
    }
}
