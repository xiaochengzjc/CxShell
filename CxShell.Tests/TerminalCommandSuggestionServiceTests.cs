using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class TerminalCommandSuggestionServiceTests
{
    [Fact]
    public void PrefersTheMostRecentHistoryPrefixOverQuickCommands()
    {
        var result = TerminalCommandSuggestionService.FindBest(
            "git ch",
            ["git checkout -b old-feature", "git checkout -b feature", "git status"],
            [new QuickCommandItem("Quick checkout", "git checkout")]);

        Assert.Equal("git checkout -b feature", result);
    }

    [Fact]
    public void FallsBackToQuickCommandsAndDeduplicatesCaseInsensitively()
    {
        var result = TerminalCommandSuggestionService.FindBest(
            "sys",
            ["hostname"],
            [
                new QuickCommandItem("System info", "SYSTEMINFO"),
                new QuickCommandItem("System info (list)", "systeminfo /fo list")
            ]);

        Assert.Equal("SYSTEMINFO", result);
    }

    [Fact]
    public void DoesNotSuggestForShortOrCompleteInput()
    {
        Assert.Null(TerminalCommandSuggestionService.FindBest("l", ["ls -la"], null));
        Assert.Null(TerminalCommandSuggestionService.FindBest("ls -la", ["ls -la"], null));
    }
}
