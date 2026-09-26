using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

public sealed class SftpTransferQueueStore
{
    private const int MaxRecords = 200;
    private const string Collection = "sftp_transfer_queue";
    private const string ItemKey = "queue";
    private static readonly ConcurrentDictionary<string, object> SharedLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _legacyPath;
    private readonly SqliteAppDataStore _store;
    private readonly object _syncRoot;

    public SftpTransferQueueStore(string? storagePath = null)
    {
        var directory = string.IsNullOrWhiteSpace(storagePath)
            ? ResolveStorageDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(storagePath))!;
        _legacyPath = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(directory, "sftp-transfer-queue.json")
            : Path.GetFullPath(storagePath);
        _store = new SqliteAppDataStore(directory);
        _syncRoot = SharedLocks.GetOrAdd(_store.DatabasePath, static _ => new object());
    }

    public IReadOnlyList<SftpTransferQueueRecord> Load()
    {
        lock (_syncRoot)
        {
            return LoadCore()
                .OrderByDescending(record => record.UpdatedAt)
                .ToList();
        }
    }

    public void Upsert(SftpTransferQueueRecord record)
    {
        if (record.TaskId == Guid.Empty)
            return;

        lock (_syncRoot)
        {
            var records = LoadCore();
            records.RemoveAll(item => item.TaskId == record.TaskId);
            record.UpdatedAt = DateTimeOffset.UtcNow;
            records.Add(record);
            SaveCore(records);
        }
    }

    public void Remove(Guid taskId)
    {
        if (taskId == Guid.Empty)
            return;

        lock (_syncRoot)
        {
            var records = LoadCore();
            if (records.RemoveAll(record => record.TaskId == taskId) > 0)
                SaveCore(records);
        }
    }

    private List<SftpTransferQueueRecord> LoadCore()
    {
        try
        {
            var payload = _store.Read(Collection, ItemKey);
            if (payload == null)
            {
                _store.ImportLegacyFile(Collection, ItemKey, _legacyPath, IsValidLegacyQueue);
                payload = _store.Read(Collection, ItemKey);
            }

            if (payload == null)
                return [];

            var data = JsonSerializer.Deserialize<SftpTransferQueueData>(payload);
            if (data == null ||
                !string.Equals(data.Format, "CxShell.SftpTransferQueue", StringComparison.Ordinal) ||
                !string.Equals(data.Version, "1.0", StringComparison.Ordinal))
                return [];

            return (data.Transfers ?? [])
                .Where(record => record.TaskId != Guid.Empty)
                .GroupBy(record => record.TaskId)
                .Select(group => group.OrderByDescending(record => record.UpdatedAt).First())
                .OrderByDescending(record => record.UpdatedAt)
                .Take(MaxRecords)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private void SaveCore(IEnumerable<SftpTransferQueueRecord> records)
    {
        try
        {
            var data = new SftpTransferQueueData
            {
                Transfers = records
                    .OrderByDescending(record => record.UpdatedAt)
                    .Take(MaxRecords)
                    .ToList()
            };
            _store.Write(Collection, ItemKey, JsonSerializer.Serialize(data));
        }
        catch
        {
            // Transfer persistence must not interrupt an active SFTP operation.
        }
    }

    private static bool IsValidLegacyQueue(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<SftpTransferQueueData>(json);
            return data != null &&
                   string.Equals(data.Format, "CxShell.SftpTransferQueue", StringComparison.Ordinal) &&
                   string.Equals(data.Version, "1.0", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ResolveStorageDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData) && Path.IsPathFullyQualified(appData))
            return Path.Combine(appData, "CxShell");

        return Path.Combine(AppContext.BaseDirectory, ".cxshell-data");
    }
}
