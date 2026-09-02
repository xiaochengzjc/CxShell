using System.Text.RegularExpressions;
using CxShell.Models;

namespace CxShell.Services.Agent;

public enum AgentPermissionDecision
{
    Allowed,
    ExecutionDisabled,
    UnsupportedProtocol,
    EmptyCommand,
    DangerousCommand,
    ChangeCommandApprovalRequired,
    CommandApprovalRequired,
    ReadOnlyMode,
    CommandBlocked,
    NotInAllowList
}

public enum AgentCommandRisk
{
    ReadOnly,
    Low = ReadOnly,
    Change,
    Dangerous
}

public sealed record AgentPermissionResult(
    AgentPermissionDecision Decision,
    string Reason,
    AgentCommandRisk Risk = AgentCommandRisk.Low,
    bool ApprovalRequired = false)
{
    public bool IsAllowed => Decision == AgentPermissionDecision.Allowed;
}

public sealed class AgentPermissionPolicy
{
    public const string AskBeforeEachCommandMode = "ask";
    public const string RiskBasedApprovalMode = "risk";
    public const string FullAccessMode = "full";

    private static readonly Regex DangerousCommandPattern = new(
        @"(^|[;&|]\s*)(sudo\s+)?(rm\b[^\r\n;&|]*\s-[^\r\n;&|]*r[^\r\n;&|]*|shutdown\b|reboot\b|poweroff\b|halt\b|mkfs(?:\s|$)|dd\s+if=|format(?:\s|$)|del\s+/[sq]|rmdir\s+/[sq]|stop-computer\b|restart-computer\b|remove-item\b.*-recurse)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SudoOptionPrefixPattern = new(
        @"(?<![A-Za-z0-9_-])(?<prefix>(?:sudo|doas)\s+)(?:(?:-n|--non-interactive|-S|--stdin|-H|-E|--preserve-env)\s+|(?:-p|--prompt|-u|--user|-g|--group)\s+(?:''|""[^"" ]*""|'[^']*'|[^\s;&|]+)\s+)+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool AllowCommandExecution { get; set; } = true;
    /// <summary>
    /// Empty keeps the legacy boolean approval behavior for callers that do
    /// not use the application settings. The application supplies one of the
    /// three explicit modes above.
    /// </summary>
    public string PermissionMode { get; set; } = string.Empty;
    public bool RequireApprovalForDangerousCommands { get; set; } = true;
    public bool RequireApprovalForChangeCommands { get; set; }
    public bool ReadOnlyMode { get; set; }
    public string AllowedCommandPrefixes { get; set; } = string.Empty;
    public string BlockedCommandPrefixes { get; set; } = string.Empty;

    public AgentPermissionResult Evaluate(AgentSessionSnapshot session, string? command)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!AllowCommandExecution)
            return new(AgentPermissionDecision.ExecutionDisabled, "Agent command execution is disabled.");

        if (session.Protocol != SessionProtocol.SSH)
            return new(AgentPermissionDecision.UnsupportedProtocol, "Only SSH terminal sessions are available to the agent.");

        if (string.IsNullOrWhiteSpace(command))
            return new(AgentPermissionDecision.EmptyCommand, "The command cannot be empty.");

        var normalizedCommand = command.Trim();
        var risk = Classify(normalizedCommand);
        if (MatchesAnyPrefix(normalizedCommand, BlockedCommandPrefixes))
        {
            return new(
                AgentPermissionDecision.CommandBlocked,
                "The command matches the Agent blocked-command list.",
                risk);
        }

        var allowedPrefixes = ParsePrefixes(AllowedCommandPrefixes);
        if (allowedPrefixes.Count > 0 &&
            !allowedPrefixes.Any(prefix => MatchesPrefix(normalizedCommand, prefix)))
        {
            return new(
                AgentPermissionDecision.NotInAllowList,
                "The command is not included in the Agent allow list.",
                risk);
        }

        if (ReadOnlyMode && risk != AgentCommandRisk.ReadOnly)
        {
            return new(
                AgentPermissionDecision.ReadOnlyMode,
                "Agent read-only mode allows inspection commands only.",
                risk);
        }

        if (string.Equals(
                NormalizePermissionMode(PermissionMode),
                AskBeforeEachCommandMode,
                StringComparison.Ordinal))
        {
            return new(
                AgentPermissionDecision.CommandApprovalRequired,
                "This command requires explicit approval.",
                risk,
                ApprovalRequired: true);
        }

        if (string.Equals(
                NormalizePermissionMode(PermissionMode),
                FullAccessMode,
                StringComparison.Ordinal))
        {
            return new(
                AgentPermissionDecision.Allowed,
                "Command dispatch is allowed by the full-access mode.",
                risk);
        }

        if (string.Equals(
                NormalizePermissionMode(PermissionMode),
                RiskBasedApprovalMode,
                StringComparison.Ordinal))
        {
            if (risk == AgentCommandRisk.Dangerous)
            {
                return new(
                    AgentPermissionDecision.DangerousCommand,
                    "This dangerous command requires explicit approval.",
                    risk,
                    ApprovalRequired: true);
            }

            if (risk == AgentCommandRisk.Change)
            {
                return new(
                    AgentPermissionDecision.ChangeCommandApprovalRequired,
                    "This modifying command requires explicit approval.",
                    risk,
                    ApprovalRequired: true);
            }

            return new(AgentPermissionDecision.Allowed, "Command dispatch is allowed.", risk);
        }

        if (risk == AgentCommandRisk.Dangerous)
        {
            if (!RequireApprovalForDangerousCommands)
            {
                return new(
                    AgentPermissionDecision.Allowed,
                    "The command was classified as dangerous, but approval is disabled.",
                    risk);
            }

            return new(
                AgentPermissionDecision.DangerousCommand,
                "This command requires explicit approval.",
                risk,
                ApprovalRequired: true);
        }

        if (risk == AgentCommandRisk.Change && RequireApprovalForChangeCommands)
        {
            return new(
                AgentPermissionDecision.ChangeCommandApprovalRequired,
                "This modifying command requires explicit approval.",
                risk,
                ApprovalRequired: true);
        }

        return new(AgentPermissionDecision.Allowed, "Command dispatch is allowed.", risk);
    }

    public static string NormalizePermissionMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            AskBeforeEachCommandMode => AskBeforeEachCommandMode,
            RiskBasedApprovalMode => RiskBasedApprovalMode,
            FullAccessMode => FullAccessMode,
            _ => string.Empty
        };

    private static AgentCommandRisk Classify(string command)
    {
        var normalizedCommand = NormalizeSudoOptions(command);
        if (DangerousCommandPattern.IsMatch(normalizedCommand))
            return AgentCommandRisk.Dangerous;

        return ChangeCommandPattern.IsMatch(normalizedCommand) ||
               OutputFileRedirectionPattern.IsMatch(normalizedCommand) ||
               TeeCommandPattern.IsMatch(normalizedCommand)
            ? AgentCommandRisk.Change
            : AgentCommandRisk.ReadOnly;
    }

    private static string NormalizeSudoOptions(string command)
        => SudoOptionPrefixPattern.Replace(command, "${prefix}");

    private static readonly Regex ChangeCommandPattern = new(
        @"(^|[;&|]\s*)(sudo\s+)?(apt(-get)?\s+(install|remove|purge|update|upgrade)|dnf\s+(install|remove|upgrade)|yum\s+(install|remove|update|upgrade)|pacman\s+-S|systemctl\s+(start|stop|restart|enable|disable)|service\s+[^\r\n;&|]+\s+(start|stop|restart)|mkdir\b|touch\b|cp\b|mv\b|ln\b|chmod\b|chown\b|kill\b|user(add|del|mod)\b|powershell(?:\.exe)?\s+.*\b(Set|New|Remove|Start|Stop|Restart|Enable|Disable)-|net\s+(start|stop)|sc\s+(create|config|delete)|reg\s+(add|delete))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OutputFileRedirectionPattern = new(
        @"(?<![A-Za-z0-9_])(?:[0-9]+)?>{1,2}(?!\s*&)(?!\s*/dev/null(?=\s|$|[;&|(){}\[\]""']))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TeeCommandPattern = new(
        @"(^|[;&|]\s*)(?:sudo\s+)?tee\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool MatchesAnyPrefix(string command, string configuredPrefixes)
        => ParsePrefixes(configuredPrefixes).Any(prefix => MatchesPrefix(command, prefix));

    private static bool MatchesPrefix(string command, string prefix)
        => command.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
           (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (prefix.EndsWith(' ') ||
             command.Length > prefix.Length && char.IsWhiteSpace(command[prefix.Length])));

    private static IReadOnlyList<string> ParsePrefixes(string configuredPrefixes)
        => (configuredPrefixes ?? string.Empty)
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(prefix => prefix.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
