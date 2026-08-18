using System;
using System.IO;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SftpTransferQueueStoreTests
{
    [Fact]
    public void UpsertAndLoadRoundTripWithoutCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "queue.json");

        try
        {
            var taskId = Guid.NewGuid();
            var store = new SftpTransferQueueStore(path);
            store.Upsert(new SftpTransferQueueRecord
            {
                TaskId = taskId,
                SessionId = Guid.NewGuid(),
                Protocol = nameof(SessionProtocol.SFTP),
                Direction = "Download",
                FileName = "large.log",
                LocalPath = "C:\\Downloads\\large.log",
                RemotePath = "/var/log/large.log",
                Host = "server.example",
                Port = 22,
                Username = "ops",
                TotalBytes = 1000,
                TransferredBytes = 400,
                ErrorMessage = "connection reset"
            });

            var records = store.Load();
            Assert.Single(records);
            Assert.Equal(taskId, records[0].TaskId);
            Assert.Equal(400, records[0].TransferredBytes);
            Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            store.Remove(taskId);
            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InvalidOrForeignFormatIsIgnored()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "queue.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{\"format\":\"Other.Queue\",\"version\":\"1.0\"}");

            var store = new SftpTransferQueueStore(path);

            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
