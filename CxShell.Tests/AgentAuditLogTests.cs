using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentAuditLogTests
{
    [Fact]
    public void AuditLogPersistsEncryptedBoundedEntriesWithoutRawSecrets()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "agent-audit.json");
        var request = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            Command = "sudo apt install nginx",
            SensitiveInput = "operator-password"
        };
        var result = new AgentCommandResult
        {
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            Status = AgentCommandStatus.Failed,
            Message = "sudo: password=operator-password was rejected"
        };

        var log = new AgentAuditLog(path);
        log.Record(request, result);

        var stored = File.ReadAllText(path);
        Assert.StartsWith("cxaes:", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-password", stored, StringComparison.Ordinal);

        var loaded = new AgentAuditLog(path).ReadRecent();
        var entry = Assert.Single(loaded);
        Assert.DoesNotContain("operator-password", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("[redacted]", entry.Detail, StringComparison.Ordinal);
        Assert.Equal(request.Command.Length, entry.CommandLength);
    }

    [Fact]
    public void AuditLogKeepsNewestFiveHundredEntries()
    {
        var log = new AgentAuditLog();
        for (var index = 0; index < AgentAuditLog.MaximumEntries + 7; index++)
        {
            var request = new AgentCommandRequest
            {
                RequestId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                Command = $"printf item-{index}"
            };
            log.Record(
                request,
                new AgentCommandResult
                {
                    RequestId = request.RequestId,
                    SessionId = request.SessionId,
                    Status = AgentCommandStatus.Sent,
                    Message = $"item-{index}"
                });
        }

        var entries = log.ReadRecent();
        Assert.Equal(AgentAuditLog.MaximumEntries, entries.Count);
        Assert.Contains("item-506", entries[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(entries, entry => entry.Detail.Contains("item-0", StringComparison.Ordinal));
    }

    [Fact]
    public void AuditLogIgnoresCorruptEncryptedFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "agent-audit.json");
        File.WriteAllText(path, "cxaes:not-valid-base64");

        var log = new AgentAuditLog(path);

        Assert.Empty(log.ReadRecent());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cxshell-audit-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
