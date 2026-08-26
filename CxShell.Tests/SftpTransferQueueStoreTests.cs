using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    [Fact]
    public void LoadKeepsTheNewestRecordWhenTaskIdIsDuplicated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "queue.json");
        var taskId = Guid.NewGuid();

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, $$"""
            {
              "Format": "CxShell.SftpTransferQueue",
              "Version": "1.0",
              "Transfers": [
                {
                  "TaskId": "{{taskId}}",
                  "SessionId": "{{Guid.NewGuid()}}",
                  "Direction": "Download",
                  "FileName": "large.log",
                  "LocalPath": "C:\\Downloads\\large.log",
                  "RemotePath": "/var/log/large.log",
                  "TransferredBytes": 20,
                  "UpdatedAt": "2026-08-25T01:00:00+00:00"
                },
                {
                  "TaskId": "{{taskId}}",
                  "SessionId": "{{Guid.NewGuid()}}",
                  "Direction": "Download",
                  "FileName": "large.log",
                  "LocalPath": "C:\\Downloads\\large.log",
                  "RemotePath": "/var/log/large.log",
                  "TransferredBytes": 80,
                  "UpdatedAt": "2026-08-25T02:00:00+00:00"
                }
              ]
            }
            """);

            var records = new SftpTransferQueueStore(path).Load();

            Assert.Single(records);
            Assert.Equal(80, records[0].TransferredBytes);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MultipleStoreInstancesCanUpsertTheSameFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "queue.json");

        try
        {
            var stores = Enumerable.Range(0, 4)
                .Select(_ => new SftpTransferQueueStore(path))
                .ToArray();

            await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            {
                stores[index % stores.Length].Upsert(new SftpTransferQueueRecord
                {
                    TaskId = Guid.NewGuid(),
                    SessionId = Guid.NewGuid(),
                    Protocol = nameof(SessionProtocol.SFTP),
                    Direction = "Upload",
                    FileName = $"file-{index}.bin",
                    Host = "server.example",
                    Port = 22,
                    Username = "ops"
                });
            })));

            var records = stores[0].Load();
            Assert.Equal(40, records.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
