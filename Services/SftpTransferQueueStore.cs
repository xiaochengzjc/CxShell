using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

public sealed class SftpTransferQueueStore
{
    private const string CurrentAppDirectoryName = "CxShell";
    private const int MaxRecords = 200;
    private readonly object _syncRoot = new();
    private readonly string _storagePath;

    public SftpTransferQueueStore(string? storagePath = null)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(ResolveStorageDirectory(), "sftp-transfer-queue.json")
            : Path.GetFullPath(storagePath);
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
            if (!File.Exists(_storagePath))
                return new List<SftpTransferQueueRecord>();

            var json = File.ReadAllText(_storagePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<SftpTransferQueueData>(json);
            if (data == null ||
                !string.Equals(data.Format, "CxShell.SftpTransferQueue", StringComparison.Ordinal) ||
                !string.Equals(data.Version, "1.0", StringComparison.Ordinal))
            {
                return new List<SftpTransferQueueRecord>();
            }

            return (data.Transfers ?? new List<SftpTransferQueueRecord>())
                .Where(record => record.TaskId != Guid.Empty)
                .Take(MaxRecords)
                .ToList();
        }
        catch
        {
            return new List<SftpTransferQueueRecord>();
        }
    }

    private void SaveCore(IEnumerable<SftpTransferQueueRecord> records)
    {
        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            var data = new SftpTransferQueueData
            {
                Transfers = records
                    .OrderByDescending(record => record.UpdatedAt)
                    .Take(MaxRecords)
                    .ToList()
            };
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(data, options);
            var temporaryPath = _storagePath + ".tmp";
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, _storagePath, true);
        }
        catch
        {
            TryDeleteTemporaryFile();
        }
    }

    private void TryDeleteTemporaryFile()
    {
        try
        {
            var temporaryPath = _storagePath + ".tmp";
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        catch
        {
        }
    }

    private static string ResolveStorageDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData) && Path.IsPathFullyQualified(appData))
            return Path.Combine(appData, CurrentAppDirectoryName);

        return Path.Combine(AppContext.BaseDirectory, ".cxshell-data");
    }
}
