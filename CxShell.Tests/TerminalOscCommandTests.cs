using CxShell.Terminal;
using System.Text;

namespace CxShell.Tests;

public sealed class TerminalOscCommandTests
{
    [Theory]
    [InlineData("0;PowerShell", "PowerShell")]
    [InlineData("2; administrator@server ", "administrator@server")]
    public void TryParseTitle_AcceptsStandardTitleOperations(string command, string expected)
    {
        Assert.True(TerminalOscCommand.TryParseTitle(command, out var title));
        Assert.Equal(expected, title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("7;file:///home/user")]
    [InlineData("invalid;title")]
    public void TryParseTitle_RejectsNonTitleCommands(string command)
    {
        Assert.False(TerminalOscCommand.TryParseTitle(command, out var title));
        Assert.Empty(title);
    }

    [Fact]
    public void TryParseTitle_FiltersControlsAndAllowsClearingTheTitle()
    {
        Assert.True(TerminalOscCommand.TryParseTitle("2;\u0001 title\u0007 ", out var filtered));
        Assert.Equal("title", filtered);

        Assert.True(TerminalOscCommand.TryParseTitle("0;", out var empty));
        Assert.Empty(empty);
    }

    [Fact]
    public void TryParseTitle_TruncatesLongTitles()
    {
        Assert.True(TerminalOscCommand.TryParseTitle("2;" + new string('a', 300), out var title));
        Assert.Equal(TerminalOscCommand.MaximumTitleLength, title.Length);
    }

    [Fact]
    public void TryParseClipboard_DecodesClipboardSelection()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("中文 from tmux"));

        Assert.True(TerminalOscCommand.TryParseClipboard($"52;c;{payload}", out var text));
        Assert.Equal("中文 from tmux", text);
    }

    [Theory]
    [InlineData("52;c;?")]
    [InlineData("52;p;not-base64")]
    [InlineData("52;clipboard;dGVzdA==")]
    public void TryParseClipboard_RejectsQueriesInvalidPayloadsAndUnknownSelections(string command)
    {
        Assert.False(TerminalOscCommand.TryParseClipboard(command, out var text));
        Assert.Empty(text);
    }

    [Fact]
    public void TryParseClipboard_RejectsOversizedPayload()
    {
        var payload = Convert.ToBase64String(new byte[TerminalOscCommand.MaximumClipboardBytes + 1]);

        Assert.False(TerminalOscCommand.TryParseClipboard($"52;c;{payload}", out var text));
        Assert.Empty(text);
    }

    [Theory]
    [InlineData("7;file:///home/user/project", "/home/user/project")]
    [InlineData("7;file://server/home/user/project%20files", "/home/user/project files")]
    public void TryParseCurrentDirectoryAcceptsAbsoluteFileUrls(string command, string expected)
    {
        Assert.True(TerminalOscCommand.TryParseCurrentDirectory(command, out var path));
        Assert.Equal(expected, path);
    }

    [Fact]
    public void TryParseShellIntegrationRecognizesPromptAndCommandMarkers()
    {
        Assert.True(TerminalOscCommand.TryParseShellIntegration(
            "133;A", out var promptStart));
        Assert.Equal(TerminalShellIntegrationEventKind.PromptStart, promptStart.Kind);
        Assert.Null(promptStart.ExitCode);

        Assert.True(TerminalOscCommand.TryParseShellIntegration(
            "133;D;17", out var commandFinished));
        Assert.Equal(TerminalShellIntegrationEventKind.CommandFinished, commandFinished.Kind);
        Assert.Equal(17, commandFinished.ExitCode);
    }

    [Theory]
    [InlineData("133;X")]
    [InlineData("133")]
    [InlineData("7;relative/path")]
    public void TryParseShellIntegrationRejectsUnknownMarkers(string command)
    {
        Assert.False(TerminalOscCommand.TryParseShellIntegration(command, out _));
    }
}
