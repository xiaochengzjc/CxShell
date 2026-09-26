using System.Text.RegularExpressions;
using CxShell.Models;

namespace CxShell.Services;

public static class SessionLogRetentionService
{
    private const char WildcardToken = '\uE000';
    private static readonly Regex DateTemplateToken = new(
        @"%(?:Y|m|d|t|h|M|s|N|a|l)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int CleanupExpiredFiles(
        SessionInfo session,
        int retentionDays,
        string? chosenPath = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (retentionDays <= 0)
            return 0;

        try
        {
            retentionDays = Math.Min(retentionDays, 3650);
            var activePath = SessionLogWriter.ResolveLogPath(session, chosenPath);
            var directory = Path.GetDirectoryName(activePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return 0;

            var template = string.IsNullOrWhiteSpace(chosenPath)
                ? session.AdvancedLogFilePath
                : chosenPath;
            if (string.IsNullOrWhiteSpace(template))
                template = "%n_%Y-%m-%d_%t.log";

            var fileNameTemplate = Path.GetFileName(template);
            var wildcardTemplate = DateTemplateToken.Replace(
                fileNameTemplate,
                WildcardToken.ToString());
            var expandedPattern = SessionLogWriter.ExpandTemplate(
                wildcardTemplate,
                session,
                (nowUtc ?? DateTimeOffset.UtcNow).LocalDateTime,
                1);
            var searchPattern = SanitizeFilePattern(expandedPattern);
            var fullActivePath = Path.GetFullPath(activePath);
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime.AddDays(-retentionDays);
            var deletedCount = 0;

            foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (pathComparer.Equals(Path.GetFullPath(path), fullActivePath))
                    continue;

                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoffUtc)
                        continue;

                    File.Delete(path);
                    deletedCount++;
                }
                catch (IOException)
                {
                    // Retention is best-effort and must not prevent logging.
                }
                catch (UnauthorizedAccessException)
                {
                    // Retention is best-effort and must not prevent logging.
                }
            }

            return deletedCount;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
    }

    private static string SanitizeFilePattern(string pattern)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            if (invalidCharacter != WildcardToken)
                pattern = pattern.Replace(invalidCharacter, '_');
        }

        return pattern.Replace(WildcardToken, '*');
    }
}
