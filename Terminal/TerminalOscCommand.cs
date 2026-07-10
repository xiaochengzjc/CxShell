using System;
using System.Text;

namespace CxShell.Terminal;

public static class TerminalOscCommand
{
    public const int MaximumTitleLength = 160;

    public static bool TryParseTitle(string command, out string title)
    {
        title = string.Empty;
        if (string.IsNullOrEmpty(command))
            return false;

        var separator = command.IndexOf(';');
        if (separator <= 0)
            return false;

        var operation = command[..separator];
        if (!string.Equals(operation, "0", StringComparison.Ordinal) &&
            !string.Equals(operation, "2", StringComparison.Ordinal))
        {
            return false;
        }

        var builder = new StringBuilder(Math.Min(command.Length - separator - 1, MaximumTitleLength));
        foreach (var ch in command.AsSpan(separator + 1))
        {
            if (!char.IsControl(ch))
                builder.Append(ch);

            if (builder.Length == MaximumTitleLength)
                break;
        }

        title = builder.ToString().Trim();
        return true;
    }
}
