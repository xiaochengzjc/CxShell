using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CxShell.Models;
using CxShell.Services;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class ConnectionAuditTests
{
    [Fact]
    public void Record_ReadsNewestFirstAndKeepsTheConfiguredLimit()
    {
        var path = CreateTempPath();
        try
        {
            var service = new ConnectionAuditService(path);
            var session = CreateSession();

            for (var index = 0; index < ConnectionAuditService.MaximumEntries + 7; index++)
                service.Record(session, ConnectionAuditEventType.Connected, index.ToString());

            var entries = service.ReadRecent();

            Assert.Equal(ConnectionAuditService.MaximumEntries, entries.Count);
            Assert.Equal("506", entries[0].Detail);
            Assert.Equal("7", entries[^1].Detail);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void ReadRecent_ReturnsEmptyForCorruptStorage()
    {
        var path = CreateTempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not-json");

            var entries = new ConnectionAuditService(path).ReadRecent();

            Assert.Empty(entries);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void ReadRecentSuccessfulConnections_ReturnsNewestUniqueSuccessfulSessions()
    {
        var path = CreateTempPath();
        try
        {
            var service = new ConnectionAuditService(path);
            var first = CreateSession();
            first.Name = "first";
            var second = CreateSession();
            second.Name = "second";

            service.Record(first, ConnectionAuditEventType.Connected, "old");
            service.Record(second, ConnectionAuditEventType.Failed, "ignored");
            service.Record(second, ConnectionAuditEventType.Connected, "newer");
            service.Record(first, ConnectionAuditEventType.Connected, "newest");

            var entries = service.ReadRecentSuccessfulConnections();

            Assert.Equal(2, entries.Count);
            Assert.Equal(first.Id, entries[0].SessionId);
            Assert.Equal("newest", entries[0].Detail);
            Assert.Equal(second.Id, entries[1].SessionId);
            Assert.Equal("newer", entries[1].Detail);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void ViewModel_FailureFilterOnlyShowsFailedEvents()
    {
        var path = CreateTempPath();
        try
        {
            var service = new ConnectionAuditService(path);
            var session = CreateSession();
            service.Record(session, ConnectionAuditEventType.Connected);
            service.Record(session, ConnectionAuditEventType.Failed, "bad password");

            var viewModel = new ConnectionAuditViewModel(service);
            viewModel.ShowFailuresOnly = true;

            Assert.Single(viewModel.Entries);
            Assert.Equal(ConnectionAuditEventType.Failed, viewModel.Entries.Single().Entry.EventType);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void RecordExternalLaunch_WritesSourceAndNeverWritesCredential()
    {
        var path = CreateTempPath();
        try
        {
            var service = new ConnectionAuditService(path);
            var request = new ExternalLaunchRequest
            {
                Scheme = "ssh",
                Protocol = SessionProtocol.SSH,
                Host = "example.test",
                Port = 22,
                Username = "ops",
                Password = "one-time-secret",
                Origin = ExternalLaunchOrigin.UrlProtocol
            };

            service.RecordExternalLaunch(request, "accepted");

            var entry = service.ReadRecent().Single();
            Assert.Equal("external", entry.Source);
            Assert.Equal(nameof(ExternalLaunchOrigin.UrlProtocol), entry.ExternalOrigin);
            Assert.True(entry.CredentialSupplied);
            var stored = new SqliteAppDataStore(Path.GetDirectoryName(path))
                .Read("connection_audit", "entries")!;
            Assert.DoesNotContain("one-time-secret", stored);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void ReadRecent_MigratesLegacyAuditJsonAndArchivesIt()
    {
        var path = CreateTempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var legacyEntry = new ConnectionAuditEntry
            {
                SessionId = Guid.NewGuid(),
                SessionName = "legacy server",
                Protocol = SessionProtocol.SSH,
                Host = "example.test",
                Port = 22,
                Username = "operator",
                EventType = ConnectionAuditEventType.Connected
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { legacyEntry }));

            var loaded = Assert.Single(new ConnectionAuditService(path).ReadRecent());

            Assert.Equal(legacyEntry.SessionId, loaded.SessionId);
            Assert.Equal(legacyEntry.Host, loaded.Host);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".migrated.bak"));
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static SessionInfo CreateSession()
    {
        return new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "audit-test",
            Host = "example.test",
            Port = 22,
            Username = "tester",
            Protocol = SessionProtocol.SSH
        };
    }

    private static string CreateTempPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "CxShellTests",
            Guid.NewGuid().ToString("N"),
            "connection-audit.json");
    }

    private static void DeleteTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
