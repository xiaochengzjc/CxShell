using CxShell.Services;

namespace CxShell.Tests;

public sealed class TerminalCommandHistoryTests
{
    [Fact]
    public void PreviousAndNextRestoreTheUnsubmittedDraft()
    {
        var history = new TerminalCommandHistory();
        history.Add("first");
        history.Add("second");

        Assert.Equal("second", history.MovePrevious(string.Empty));
        Assert.Equal("first", history.MovePrevious("second"));
        Assert.Equal("second", history.MoveNext());
        Assert.Equal(string.Empty, history.MoveNext());
        Assert.False(history.IsNavigating);
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
        Assert.Equal("new", history.MovePrevious(string.Empty));
        Assert.Equal("old", history.MovePrevious("new"));
        Assert.Equal("old", history.MovePrevious("old"));
    }

    [Fact]
    public void PrefixNavigationOnlyReturnsMatchingHistoryEntries()
    {
        var history = new TerminalCommandHistory();
        history.Add("git status");
        history.Add("ls -la");
        history.Add("git checkout main");

        Assert.Equal("git checkout main", history.MovePrevious("git"));
        Assert.Equal("git status", history.MovePrevious("git checkout main"));
        Assert.Equal("git checkout main", history.MoveNext());
        Assert.Equal("git", history.MoveNext());
        Assert.False(history.IsNavigating);
    }

    [Fact]
    public void ResetNavigationMakesTheNextPreviousStartAtTheNewestCommand()
    {
        var history = new TerminalCommandHistory();
        history.Add("first");
        history.Add("second");

        Assert.Equal("second", history.MovePrevious(string.Empty));
        history.ResetNavigation();

        Assert.Null(history.MovePrevious("new draft"));
        history.ResetNavigation();
        Assert.Equal("second", history.MovePrevious(string.Empty));
    }
}
