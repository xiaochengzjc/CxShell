using System.Text.RegularExpressions;

namespace CxShell.Services.Agent;

/// <summary>
/// Normalizes remote command timeouts at the Agent boundary. Package managers
/// and installers frequently spend longer than a model's suggested timeout,
/// especially on small or embedded hosts.
/// </summary>
public static class AgentCommandTimeoutPolicy
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LongRunningDefaultTimeout = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan LongRunningMinimumTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(30);

    private static readonly Regex LongRunningCommandPattern = new(
        @"(^|[;&|]\s*)(sudo\s+)?(?:" +
        @"apt(?:-get)?\s+(?:update|upgrade|full-upgrade|dist-upgrade|install|remove|purge)|" +
        @"dnf\s+(?:upgrade|update|install|remove)|" +
        @"yum\s+(?:update|upgrade|install|remove)|" +
        @"zypper\s+(?:update|refresh|install|remove)|" +
        @"apk\s+(?:add|del|upgrade)|" +
        @"pacman\s+-S(?:u|yu)?\b|" +
        @"brew\s+(?:install|upgrade|update)|" +
        @"choco(?:latey)?\s+install\b|" +
        @"winget\s+install\b|" +
        @"msiexec(?:\.exe)?\b|" +
        @"(?:python3?|pip3?|npm|pnpm|yarn|dotnet)\s+(?:install|update|upgrade)\b" +
        @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static TimeSpan Resolve(
        string? command,
        TimeSpan requestedTimeout,
        bool hasExplicitTimeout)
    {
        var bounded = Clamp(requestedTimeout);
        if (!IsLongRunning(command))
            return bounded;

        var minimum = hasExplicitTimeout
            ? LongRunningMinimumTimeout
            : LongRunningDefaultTimeout;
        return bounded < minimum ? minimum : bounded;
    }

    public static bool IsLongRunning(string? command)
        => !string.IsNullOrWhiteSpace(command) &&
           LongRunningCommandPattern.IsMatch(command.Trim());

    private static TimeSpan Clamp(TimeSpan timeout)
        => TimeSpan.FromMilliseconds(Math.Clamp(
            timeout.TotalMilliseconds,
            100,
            MaximumTimeout.TotalMilliseconds));
}
