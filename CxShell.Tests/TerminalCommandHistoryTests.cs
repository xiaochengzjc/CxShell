using CxShell.Services;

namespace CxShell.Tests;

public sealed class TerminalCommandHistoryTests
{
    [Fact]
    public void AddsCommandsAndExposesThemForSuggestions()
    {
        var history = new TerminalCommandHistory();
        history.Add("first");
        history.Add("second");

        Assert.Equal(["first", "second"], history.Entries);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void AdjacentDuplicatesAreCollapsedAndCapacityIsBounded()
    {
        var history = new TerminalCommandHistory(capacity: 2);
        history.Add("same");
        history.Add("same");
        history.Add("old");
        history.Add("new");

        Assert.Equal(2, history.Count);
        Assert.Equal(["old", "new"], history.Entries);
    }
}
