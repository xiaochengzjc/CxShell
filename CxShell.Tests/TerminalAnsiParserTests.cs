using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalAnsiParserTests
{
    [Fact]
    public void AlternateScreenRestoresMainScreenAndScrollback()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 3, maxScrollback: 20);
        var parser = new AnsiParser(buffer);
        parser.Process("main");
        parser.Process("\x1b[?1049h");

        Assert.True(buffer.IsAlternateScreen);
        parser.Process("vim");
        parser.Process("\x1b[?1049l");

        Assert.False(buffer.IsAlternateScreen);
        Assert.Equal('m', buffer.GetCell(0, 0).Character);
        Assert.Equal('a', buffer.GetCell(0, 1).Character);
        Assert.Equal("main", buffer.ExportText());
    }

    [Fact]
    public void AlternateScreenSequenceCanBeSplitAcrossInputChunks()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 3);
        var parser = new AnsiParser(buffer);

        parser.Process("before\x1b[?104");
        parser.Process("9hinside\x1b[?1049");
        parser.Process("lafter");

        Assert.False(buffer.IsAlternateScreen);
        Assert.Equal("beforeafter", buffer.ExportText());
    }

    [Fact]
    public void ScrollRegionScrollsOnlyTheConfiguredRows()
    {
        var buffer = new TerminalBuffer(columns: 8, rows: 4, maxScrollback: 20);
        var parser = new AnsiParser(buffer);
        parser.Process("A\r\nB\r\nC\r\nD");

        parser.Process("\x1b[2;3r\x1b[3;1HE");
        parser.Process("\r\nF");

        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal('A', buffer.GetCell(0, 0).Character);
        Assert.Equal('E', buffer.GetCell(1, 0).Character);
        Assert.Equal('F', buffer.GetCell(2, 0).Character);
        Assert.Equal('D', buffer.GetCell(3, 0).Character);
    }

    [Fact]
    public void UnknownDcsIsConsumedWithoutScreenText()
    {
        var buffer = new TerminalBuffer(columns: 30, rows: 2);
        var parser = new AnsiParser(buffer);
        parser.Process("before\x1bP1;2+qignored\x1b\\after");

        Assert.Equal("beforeafter", buffer.ExportText());
    }

    [Fact]
    public void DcsCanBeSplitAndRaisesTheCompleteCommand()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);
        string? command = null;
        parser.DeviceControlCommandReceived += value => command = value;

        parser.Process("\x1bP$q");
        parser.Process("m\x1b\\");

        Assert.Equal("$qm", command);
    }

    [Fact]
    public void MultiplePrivateModesEnableMouseAndBracketedPaste()
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
    public void DeviceStatusAndAttributesRequestsRaiseEvents()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);
        var status = -1;
        var attributes = '\0';
        parser.DeviceStatusReportRequested += value => status = value;
        parser.DeviceAttributesRequested += value => attributes = value;

        parser.Process("\x1b[6n\x1b[c\x1b[>c");

        Assert.Equal(6, status);
        Assert.Equal('>', attributes);
    }

    [Fact]
    public void OriginModeMakesCursorPositionsRelativeToScrollRegion()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 6);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[2;4r\x1b[?6h");
        Assert.Equal(1, buffer.CursorRow);

        parser.Process("\x1b[2;3H");
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);

        parser.Process("\x1b[99d");
        Assert.Equal(3, buffer.CursorRow);

        parser.Process("\x1b[?6l");
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void TabStopsCanBeSetClearedAndMovedInBothDirections()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[5G\x1bH\x1b[1G\x1b[I");
        Assert.Equal(4, buffer.CursorCol);

        parser.Process("\x1b[0g\x1b[1G\x1b[I");
        Assert.Equal(8, buffer.CursorCol);

        parser.Process("\x1b[20G\x1b[Z");
        Assert.Equal(16, buffer.CursorCol);

        parser.Process("\x1b[3g\x1b[1G\x1b[I");
        Assert.Equal(19, buffer.CursorCol);
    }

    [Fact]
    public void TuiGraphicAttributesAreStoredOnCells()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[3;7;9mX\x1b[23;27;29mY");

        var styled = buffer.GetCell(0, 0);
        Assert.True(styled.Italic);
        Assert.True(styled.Reverse);
        Assert.True(styled.Strikethrough);

        var normal = buffer.GetCell(0, 1);
        Assert.False(normal.Italic);
        Assert.False(normal.Reverse);
        Assert.False(normal.Strikethrough);
    }

    [Fact]
    public void DecalnFillsTheScreenWithoutMovingTheCursor()
    {
        var buffer = new TerminalBuffer(columns: 5, rows: 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[2;3H\x1b#8");

        Assert.Equal((1, 2), (buffer.CursorRow, buffer.CursorCol));
        for (var row = 0; row < buffer.Rows; row++)
        for (var col = 0; col < buffer.Columns; col++)
            Assert.Equal('E', buffer.GetCell(row, col).Character);
    }

    [Fact]
    public void DecscusrStoresRemoteCursorStyle()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[6 q");

        Assert.True(buffer.HasRemoteCursorStyle);
        Assert.Equal(6, buffer.CursorStyle);
    }

    [Fact]
    public void SupplementaryAndCombiningCharactersKeepTheirTerminalWidth()
    {
        var buffer = new TerminalBuffer(columns: 12, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("A\U0001F600e");
        parser.Process("\u0301");

        Assert.Equal("\U0001F600", buffer.GetCell(0, 1).GetText());
        Assert.True(buffer.GetCell(0, 2).IsWideContinuation);
        Assert.Equal("e\u0301", buffer.GetCell(0, 3).GetText());
        Assert.Equal(4, buffer.CursorCol);
        Assert.Equal("A\U0001F600e\u0301", buffer.ExportText());
    }

    [Fact]
    public void SgrStoresDimInvisibleAndDoubleUnderlineAttributes()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[2;8;21mX\x1b[22;28;24mY");

        var styled = buffer.GetCell(0, 0);
        Assert.True(styled.Dim);
        Assert.True(styled.Invisible);
        Assert.True(styled.DoubleUnderline);
        Assert.False(styled.Underline);

        var normal = buffer.GetCell(0, 1);
        Assert.False(normal.Dim);
        Assert.False(normal.Invisible);
        Assert.False(normal.DoubleUnderline);
    }

    [Fact]
    public void SavedCursorRestoresTextAttributes()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[1;2;3;8;21;31;44m\x1b7\x1b[0m\x1b8X");

        var restored = buffer.GetCell(0, 0);
        Assert.True(restored.Bold);
        Assert.True(restored.Dim);
        Assert.True(restored.Italic);
        Assert.True(restored.Invisible);
        Assert.True(restored.DoubleUnderline);
        Assert.Equal(buffer.GetAnsiColor(1), restored.Foreground);
        Assert.Equal(buffer.GetAnsiColor(4), restored.Background);
    }

    [Fact]
    public void C1ControlsAndModernModesAreConsumedWithoutScreenText()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("before\x9b>4;2m\x9b?1004h\x9b?2026hafter\x9b?2026l");

        Assert.Equal("beforeafter", buffer.ExportText());
        Assert.Equal(2, buffer.ModifyOtherKeysMode);
        Assert.True(buffer.FocusReportingMode);
        Assert.False(buffer.SynchronizedOutputMode);
    }

    [Fact]
    public void SynchronizedOutputDefersChangeNotificationUntilReleased()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2);
        var parser = new AnsiParser(buffer);
        var changes = 0;
        buffer.Changed += () => changes++;

        parser.Process("\x1b[?2026hhello\x1b[?2026l");

        Assert.Equal(1, changes);
    }

    [Fact]
    public void DecrqmReportsCurrentPrivateMode()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2);
        var parser = new AnsiParser(buffer);
        var query = (-1, -1);
        parser.DeviceModeQueryRequested += (isPrivate, mode) => query = (isPrivate ? 1 : 0, mode);

        parser.Process("\x1b[?1004h\x1b[?1004$p");

        Assert.Equal((1, 1004), query);
        Assert.True(buffer.IsModeEnabled(true, 1004));
    }

    [Fact]
    public void C1ControlStringsAreConsumedUntilStOrCan()
    {
        var buffer = new TerminalBuffer(columns: 30, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("before\u0098hidden\u009Cafter\u009Fdiscarded\u0018visible");

        Assert.Equal("beforeaftervisible", buffer.ExportText());
    }

    [Fact]
    public void Osc8HyperlinksAreStoredOnCellsAndClosedExplicitly()
    {
        var buffer = new TerminalBuffer(columns: 30, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b]8;;https://example.com\aDocs\x1b]8;;\a plain");

        Assert.Equal("https://example.com", buffer.GetCell(0, 0).HyperlinkUri);
        Assert.Equal("https://example.com", buffer.GetCell(0, 3).HyperlinkUri);
        Assert.Null(buffer.GetCell(0, 4).HyperlinkUri);
        Assert.Null(buffer.CurrentHyperlinkUri);
    }

    [Fact]
    public void ResetInputStatePreventsPartialEscapeFromEatingReconnectOutput()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("before\x1b[");
        parser.ResetInputState();
        parser.Process("after");

        Assert.Equal("beforeafter", buffer.ExportText());
    }

    [Fact]
    public void ParserBatchesChangeNotificationsForOneOutputChunk()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2);
        var parser = new AnsiParser(buffer);
        var changes = 0;
        buffer.Changed += () => changes++;

        parser.Process("a\r\nb\r\nc\r\nd");

        Assert.Equal(1, changes);
    }

    [Fact]
    public void ExtremelyLongCsiParameterIsConsumedWithoutOverflow()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[999999999999999999999999Aok");

        Assert.Equal("ok", buffer.ExportText());
    }

    [Fact]
    public void KittyKeyboardFlagsSupportSetModifyQueryAndPushPop()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);
        var queryRequested = false;
        parser.KittyKeyboardProtocolQueryRequested += () => queryRequested = true;

        parser.Process("\x1b[=1;1u");
        Assert.Equal(1, buffer.KittyKeyboardFlags);

        parser.Process("\x1b[=2;2u");
        Assert.Equal(3, buffer.KittyKeyboardFlags);

        parser.Process("\x1b[=1;3u");
        Assert.Equal(2, buffer.KittyKeyboardFlags);

        parser.Process("\x1b[?u");
        Assert.True(queryRequested);

        parser.Process("\x1b[>16u");
        Assert.Equal(16, buffer.KittyKeyboardFlags);
        parser.Process("\x1b[<u");
        Assert.Equal(0, buffer.KittyKeyboardFlags);

        parser.Process("\x1b[<u");
        Assert.Equal(0, buffer.KittyKeyboardFlags);
    }

    [Fact]
    public void KittyKeyboardFlagStacksAreIndependentAcrossAlternateScreen()
    {
        var buffer = new TerminalBuffer();
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b[=3;1u\x1b[?1049h");
        Assert.True(buffer.IsAlternateScreen);
        Assert.Equal(0, buffer.KittyKeyboardFlags);

        parser.Process("\x1b[>16u");
        Assert.Equal(16, buffer.KittyKeyboardFlags);
        parser.Process("\x1b[?1049l");

        Assert.False(buffer.IsAlternateScreen);
        Assert.Equal(3, buffer.KittyKeyboardFlags);
    }
}
