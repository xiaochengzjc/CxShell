using System.Text.RegularExpressions;

namespace CxShell.Services.Agent;

/// <summary>
/// Removes credentials from live Agent diagnostics before they are shown in
/// the Agent panel or sent back to the model. The redactor is intentionally
/// conservative and also accepts the exact in-memory credentials supplied by
/// the operator.
/// </summary>
public static partial class AgentSensitiveDataRedactor
{
    private static readonly string[] SecretKeys =
    [
        "password",
        "passwd",
        "passphrase",
        "token",
        "api_key",
        "apikey",
        "secret",
        "private_key"
    ];

    public static string Redact(
        string? text,
        IEnumerable<string>? exactSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var redacted = text;
        if (exactSecrets != null)
        {
            foreach (var secret in exactSecrets
                         .Where(value => !string.IsNullOrEmpty(value))
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(value => value.Length))
            {
                redacted = redacted.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        redacted = KeyValueSecretRegex().Replace(redacted, match =>
            $"{match.Groups["key"].Value}{match.Groups["separator"].Value}[redacted]");
        redacted = BearerTokenRegex().Replace(redacted, "$1[redacted]");
        return redacted;
    }

    public static string RedactCommand(string? command)
        => Redact(command);

    [GeneratedRegex(
        @"(?<key>password|passwd|passphrase|token|api[_-]?key|apikey|secret|private[_-]?key)(?<separator>\s*[:=]\s*)(?<value>[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();
}
