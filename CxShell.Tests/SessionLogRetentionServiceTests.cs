using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SessionLogRetentionServiceTests
{
    [Fact]
    public void CleanupExpiredFiles_DeletesOnlyExpiredFilesMatchingSessionTemplate()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var session = CreateSession(directory);
            var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
            var expiredPath = SessionLogWriter.ResolveLogPath(
                session,
                SessionLogWriter.ExpandTemplate(
                    session.AdvancedLogFilePath,
                    session,
                    now.LocalDateTime.AddDays(-40),
                    1));
            var recentPath = SessionLogWriter.ResolveLogPath(
                session,
                SessionLogWriter.ExpandTemplate(
                    session.AdvancedLogFilePath,
                    session,
                    now.LocalDateTime.AddDays(-10),
                    1));
            var unrelatedPath = Path.Combine(directory, "other_2026-08-01_120000.log");
            var activePath = SessionLogWriter.ResolveLogPath(session);

            WriteFileWithTimestamp(expiredPath, now.UtcDateTime.AddDays(-40));
            WriteFileWithTimestamp(recentPath, now.UtcDateTime.AddDays(-10));
            WriteFileWithTimestamp(unrelatedPath, now.UtcDateTime.AddDays(-40));
            WriteFileWithTimestamp(activePath, now.UtcDateTime.AddDays(-40));

            var deletedCount = SessionLogRetentionService.CleanupExpiredFiles(
                session,
                retentionDays: 30,
                nowUtc: now);

            Assert.Equal(1, deletedCount);
            Assert.False(File.Exists(expiredPath));
            Assert.True(File.Exists(recentPath));
            Assert.True(File.Exists(unrelatedPath));
            Assert.True(File.Exists(activePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CleanupExpiredFiles_ZeroRetentionDisablesCleanup()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var session = CreateSession(directory);
            var oldPath = Path.Combine(directory, "server_2026-08-01_120000.log");
            var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
            WriteFileWithTimestamp(oldPath, now.UtcDateTime.AddDays(-40));

            var deletedCount = SessionLogRetentionService.CleanupExpiredFiles(
                session,
                retentionDays: 0,
                nowUtc: now);

            Assert.Equal(0, deletedCount);
            Assert.True(File.Exists(oldPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SessionInfo CreateSession(string directory)
        => new()
        {
            Name = "server",
            AdvancedLogFilePath = Path.Combine(directory, "%n_%Y-%m-%d_%t.log")
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CxShellLogRetentionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFileWithTimestamp(string path, DateTime lastWriteTimeUtc)
    {
        File.WriteAllText(path, "test log");
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
    }
}
