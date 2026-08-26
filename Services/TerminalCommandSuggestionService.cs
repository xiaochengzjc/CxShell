using System;
using System.Collections.Generic;
using System.Linq;
using CxShell.Models;

namespace CxShell.Services;

/// <summary>
/// Finds a local command completion from the current session history and
/// configured quick commands. It never queries or executes anything remotely.
/// </summary>
public static class TerminalCommandSuggestionService
{
    public static string? FindBest(
        string? prefix,
        IEnumerable<string>? history,
        IEnumerable<QuickCommandItem>? quickCommands)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim().Length < 2)
            return null;

        var input = prefix;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateCandidates(history, quickCommands))
        {
            if (!seen.Add(candidate))
                continue;

            if (candidate.Length > input.Length &&
                candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(
        IEnumerable<string>? history,
        IEnumerable<QuickCommandItem>? quickCommands)
    {
        if (history != null)
        {
            var entries = history.ToArray();
            for (var index = entries.Length - 1; index >= 0; index--)
            {
                var normalized = entries[index]?.Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }

        if (quickCommands != null)
        {
            foreach (var command in quickCommands)
            {
                var normalized = command.CommandText?.Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }
    }
}
