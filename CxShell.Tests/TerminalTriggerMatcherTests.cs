using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class TerminalTriggerMatcherTests
{
    [Fact]
    public void IsMatch_UsesOrdinalTextMatchingByDefault()
    {
        var rule = new LoginScriptRule { Expect = "Password:" };

        Assert.True(TerminalTriggerMatcher.IsMatch(rule, "login Password: "));
        Assert.False(TerminalTriggerMatcher.IsMatch(rule, "login password: "));
    }

    [Fact]
    public void IsMatch_UsesRegexWhenEnabled()
    {
        var rule = new LoginScriptRule
        {
            Expect = @"(?:Password|Passcode):\s*$",
            IsRegex = true
        };

        Assert.True(TerminalTriggerMatcher.IsMatch(rule, "Enter Password:"));
        Assert.True(TerminalTriggerMatcher.IsMatch(rule, "Enter Passcode:\n"));
        Assert.False(TerminalTriggerMatcher.IsMatch(rule, "Enter username:"));
    }

    [Fact]
    public void IsMatch_ReturnsFalseForInvalidRegex()
    {
        var rule = new LoginScriptRule { Expect = "[", IsRegex = true };

        var exception = Record.Exception(() => TerminalTriggerMatcher.IsMatch(rule, "output"));

        Assert.Null(exception);
        Assert.False(TerminalTriggerMatcher.IsMatch(rule, "output"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void IsMatch_RejectsEmptyPatterns(string expect)
    {
        var rule = new LoginScriptRule { Expect = expect };

        Assert.False(TerminalTriggerMatcher.IsMatch(rule, "output"));
        Assert.False(TerminalTriggerMatcher.IsMatch(rule, expect));
    }
}
