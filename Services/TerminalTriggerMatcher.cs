using System.Text.RegularExpressions;
using CxShell.Models;

namespace CxShell.Services;

public static class TerminalTriggerMatcher
{
    public static bool IsMatch(LoginScriptRule rule, string output)
    {
        if (string.IsNullOrWhiteSpace(rule.Expect) || string.IsNullOrEmpty(output))
            return false;

        if (!rule.IsRegex)
            return output.Contains(rule.Expect, StringComparison.Ordinal);

        try
        {
            return Regex.IsMatch(output, rule.Expect, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
