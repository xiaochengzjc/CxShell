using CxShell.Terminal;

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
}
