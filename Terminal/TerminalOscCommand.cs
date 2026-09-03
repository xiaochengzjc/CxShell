using System;
using System.Globalization;
using System.Text;

namespace CxShell.Terminal;

public enum TerminalShellIntegrationEventKind
{
    PromptStart,
    PromptEnd,
    CommandStart,
    CommandFinished
}

public readonly record struct TerminalShellIntegrationEvent(
    TerminalShellIntegrationEventKind Kind,
    int? ExitCode);

public static class TerminalOscCommand
{
    public const int MaximumTitleLength = 160;
    public const int MaximumClipboardBytes = 1024 * 1024;
    public const int MaximumCurrentDirectoryLength = 4096;

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

    /// <summary>
    /// Parses OSC 7 file URLs emitted by shell integrations such as bash,
    /// zsh, and fish. Only absolute POSIX-style paths are accepted.
    /// </summary>
    public static bool TryParseCurrentDirectory(string command, out string path)
    {
        path = string.Empty;

        if (!command.StartsWith("7;", StringComparison.Ordinal))
            return false;

        var value = command[2..].Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string pathPart;
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = value[7..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex < 0)
                return false;

            pathPart = remainder[slashIndex..];
        }
        else
        {
            pathPart = value;
        }

        try
        {
            pathPart = Uri.UnescapeDataString(pathPart);
        }
        catch (UriFormatException)
        {
            return false;
        }

        pathPart = pathPart.Replace('\\', '/').Trim();
        if (pathPart.Length is 0 or > MaximumCurrentDirectoryLength ||
            !pathPart.StartsWith("/", StringComparison.Ordinal) ||
            pathPart.Contains('\0'))
        {
            return false;
        }

        path = pathPart;
        return true;
    }

    /// <summary>
    /// Parses the FinalTerm/OSC 133 shell integration markers. Parameters
    /// after the marker are intentionally ignored except for D's exit code.
    /// </summary>
    public static bool TryParseShellIntegration(
        string command,
        out TerminalShellIntegrationEvent integrationEvent)
    {
        integrationEvent = default;
        if (!command.StartsWith("133;", StringComparison.Ordinal))
            return false;

        var fields = command[4..].Split(';');
        if (fields.Length == 0)
            return false;

        var kind = fields[0] switch
        {
            "A" => TerminalShellIntegrationEventKind.PromptStart,
            "B" => TerminalShellIntegrationEventKind.PromptEnd,
            "C" => TerminalShellIntegrationEventKind.CommandStart,
            "D" => TerminalShellIntegrationEventKind.CommandFinished,
            _ => (TerminalShellIntegrationEventKind?)null
        };
        if (kind == null)
            return false;

        int? exitCode = null;
        if (kind == TerminalShellIntegrationEventKind.CommandFinished &&
            fields.Length > 1 &&
            int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedExitCode))
        {
            exitCode = parsedExitCode;
        }

        integrationEvent = new TerminalShellIntegrationEvent(kind.Value, exitCode);
        return true;
    }
}
