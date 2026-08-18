using System.Text.RegularExpressions;

namespace CxShell.Services;

public sealed record OpenSshJumpHost(string Host, int Port, string Username);

public sealed record OpenSshConfigEntry(
    string Alias,
    string Host,
    int Port,
    string Username,
    string? IdentityFile,
    IReadOnlyList<OpenSshJumpHost> JumpHosts);

public static class OpenSshConfigParser
{
    private static readonly Regex WildcardPattern = new(
        "^[^*?]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<OpenSshConfigEntry> Parse(string text, string? configPath = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var blocks = ParseBlocks(text);
        var aliases = blocks
            .SelectMany(block => block.Patterns)
            .Where(IsConcreteAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = new List<OpenSshConfigEntry>(aliases.Count);
        foreach (var alias in aliases)
        {
            var values = ResolveValues(alias, blocks);
            var host = GetValue(values, "hostname") ?? alias;
            var username = GetValue(values, "user") ?? string.Empty;
            var port = ParsePort(GetValue(values, "port"));
            var identityFile = ResolveIdentityFile(
                GetValue(values, "identityfile"),
                configPath,
                alias,
                username);
            var jumpHosts = ParseJumpHosts(
                GetValue(values, "proxyjump"),
                alias,
                port,
                username);

            if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "none", StringComparison.OrdinalIgnoreCase))
                continue;

            entries.Add(new OpenSshConfigEntry(
                alias,
                host,
                port,
                username,
                identityFile,
                jumpHosts));
        }

        return entries;
    }

    public static bool LooksLikeConfig(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var separator = FindWhitespace(line);
            var key = (separator < 0 ? line : line[..separator]).Trim();
            if (key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("hostname", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("port", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("identityfile", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("proxyjump", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ConfigBlock> ParseBlocks(string text)
    {
        var blocks = new List<ConfigBlock>();
        ConfigBlock? current = null;
        var continued = string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimEnd().EndsWith('\\'))
            {
                continued += line.TrimEnd()[..^1];
                continue;
            }

            line = continued + line;
            continued = string.Empty;
            line = StripComment(line).Trim();
            if (line.Length == 0)
                continue;

            var separator = FindWhitespace(line);
            if (separator < 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[separator..].Trim();
            if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                var patterns = SplitWords(value);
                if (patterns.Count == 0)
                {
                    current = null;
                    continue;
                }

                current = new ConfigBlock(patterns);
                blocks.Add(current);
                continue;
            }

            current?.SetFirstValue(key, Unquote(value));
        }

        return blocks;
    }

    private static Dictionary<string, string> ResolveValues(string alias, IReadOnlyList<ConfigBlock> blocks)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (!Matches(alias, block.Patterns))
                continue;

            foreach (var pair in block.Values)
            {
                // OpenSSH uses the first obtained value for most scalar options.
                values.TryAdd(pair.Key, pair.Value);
            }
        }

        return values;
    }

    private static bool Matches(string alias, IReadOnlyList<string> patterns)
    {
        var positiveMatch = false;
        foreach (var pattern in patterns)
        {
            var isNegative = pattern.StartsWith('!');
            var candidate = isNegative ? pattern[1..] : pattern;
            if (!MatchesWildcard(alias, candidate))
                continue;

            if (isNegative)
                return false;

            positiveMatch = true;
        }

        return positiveMatch;
    }

    private static bool MatchesWildcard(string value, string pattern)
    {
        if (pattern.Length == 0)
            return false;

        if (WildcardPattern.IsMatch(pattern))
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsConcreteAlias(string pattern)
    {
        return !pattern.StartsWith('!') &&
               !string.Equals(pattern, "*", StringComparison.Ordinal) &&
               !pattern.Contains('*') &&
               !pattern.Contains('?');
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static int ParsePort(string? value)
    {
        return int.TryParse(value, out var port) && port is >= 1 and <= 65535
            ? port
            : 22;
    }

    private static string? ResolveIdentityFile(
        string? value,
        string? configPath,
        string alias,
        string username)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = value
            .Replace("%d", home, StringComparison.OrdinalIgnoreCase)
            .Replace("%h", alias, StringComparison.OrdinalIgnoreCase)
            .Replace("%r", username, StringComparison.OrdinalIgnoreCase);

        if (expanded == "~")
            expanded = home;
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            expanded = Path.Combine(home, expanded[2..]);

        expanded = Environment.ExpandEnvironmentVariables(expanded);
        if (!Path.IsPathRooted(expanded) && !string.IsNullOrWhiteSpace(configPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (directory != null)
                expanded = Path.Combine(directory, expanded);
        }

        return Path.GetFullPath(expanded);
    }

    private static IReadOnlyList<OpenSshJumpHost> ParseJumpHosts(
        string? value,
        string alias,
        int port,
        string username)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            return [];

        var result = new List<OpenSshJumpHost>();
        foreach (var rawJump in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var jump = rawJump
                .Replace("%h", alias, StringComparison.OrdinalIgnoreCase)
                .Replace("%p", port.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("%r", username, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(jump, "none", StringComparison.OrdinalIgnoreCase))
                continue;

            var at = jump.LastIndexOf('@');
            var jumpUser = at >= 0 ? jump[..at] : string.Empty;
            var hostPort = at >= 0 ? jump[(at + 1)..] : jump;
            var (jumpHost, jumpPort) = SplitHostPort(hostPort);
            if (string.IsNullOrWhiteSpace(jumpHost))
                continue;

            result.Add(new OpenSshJumpHost(jumpHost, jumpPort, jumpUser));
        }

        return result;
    }

    private static (string Host, int Port) SplitHostPort(string value)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket > 1)
            {
                var host = value[1..closingBracket];
                var suffix = value[(closingBracket + 1)..].TrimStart(':');
                return (host, ParsePort(suffix));
            }
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out var port))
            return (value[..colon], port is >= 1 and <= 65535 ? port : 22);

        return (value, 22);
    }

    private static int FindWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
                return index;
        }

        return -1;
    }

    private static List<string> SplitWords(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var character in value)
        {
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string Unquote(string value)
    {
        var words = SplitWords(value);
        return words.Count == 0 ? string.Empty : string.Join(' ', words);
    }

    private static string StripComment(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
            {
                return value[..index];
            }
        }

        return value;
    }

    private sealed class ConfigBlock(IReadOnlyList<string> patterns)
    {
        public IReadOnlyList<string> Patterns { get; } = patterns;
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SetFirstValue(string key, string value)
        {
            Values.TryAdd(key, value);
        }
    }
}
