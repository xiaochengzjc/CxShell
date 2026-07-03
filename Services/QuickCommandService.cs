using CxShell.Models;

namespace CxShell.Services;

public static class QuickCommandService
{
    private static readonly QuickCommandItem[] PosixDefaultCommands =
    [
        new("pwd", "pwd"),
        new("ls -la", "ls -la"),
        new("df -h", "df -h"),
        new("free -h", "free -h"),
        new("top", "top")
    ];

    private static readonly QuickCommandItem[] WindowsDefaultCommands =
    [
        new("dir", "dir"),
        new("ipconfig", "ipconfig"),
        new("tasklist", "tasklist"),
        new("systeminfo", "systeminfo")
    ];

    public static IReadOnlyList<QuickCommandItem> GetCommands(SessionInfo session, bool supportsPosixShellFeatures)
    {
        var configured = ParseCustomCommands(session.AdvancedQuickCommandSet);
        if (configured.Count > 0)
            return configured;

        return supportsPosixShellFeatures
            ? PosixDefaultCommands
            : WindowsDefaultCommands;
    }

    private static List<QuickCommandItem> ParseCustomCommands(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsBuiltInSetName(value))
            return [];

        return value
            .Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseCommandLine)
            .Where(item => !string.IsNullOrWhiteSpace(item.CommandText))
            .DistinctBy(item => item.CommandText, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static QuickCommandItem ParseCommandLine(string line)
    {
        var separator = line.IndexOf('=');
        if (separator > 0 && separator < line.Length - 1)
        {
            var name = line[..separator].Trim();
            var command = line[(separator + 1)..].Trim();
            return new QuickCommandItem(string.IsNullOrWhiteSpace(name) ? command : name, command);
        }

        return new QuickCommandItem(line.Trim(), line.Trim());
    }

    private static bool IsBuiltInSetName(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ||
               string.Equals(text, "Default Quick Command Set", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "<<All Commands>>", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "<<所有命令>>", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "所有命令", StringComparison.OrdinalIgnoreCase);
    }
}
