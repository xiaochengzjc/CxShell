using System;
using System.Text;

namespace CxShell.Terminal;

public static class TerminalOscCommand
{
    public const int MaximumTitleLength = 160;
    public const int MaximumClipboardBytes = 1024 * 1024;

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

    public static bool TryParseClipboard(string command, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(command) || !command.StartsWith("52;", StringComparison.Ordinal))
            return false;

        var selectionSeparator = command.IndexOf(';', 3);
        if (selectionSeparator <= 3)
            return false;

        var selection = command[3..selectionSeparator];
        if (selection.Contains('?') ||
            selection.Any(character => !"cps01234567".Contains(character)) ||
            (!selection.Contains('c') && !selection.Contains('p') && !selection.Contains('s')))
        {
            return false;
        }

        var payload = command[(selectionSeparator + 1)..];
        if (payload == "?" || payload.Length > MaximumClipboardBytes * 2)
            return false;

        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length > MaximumClipboardBytes)
                return false;

            text = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
