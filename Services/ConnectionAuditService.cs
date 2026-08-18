using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

public sealed class ConnectionAuditService
{
    public const int MaximumEntries = 500;

    private readonly object _gate = new();
    private readonly string _path;
    private List<ConnectionAuditEntry>? _entries;

    public ConnectionAuditService(string? storagePath = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(ResolveStorageDirectory(), "connection-audit.json")
            : Path.GetFullPath(storagePath);
    }

    public string StoragePath => _path;

    public IReadOnlyList<ConnectionAuditEntry> ReadRecent(int limit = MaximumEntries)
    {
        lock (_gate)
        {
            try
            {
                return GetEntriesUnsafe()
                    .Take(Math.Clamp(limit, 1, MaximumEntries))
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CxShell audit read failed: {ex.Message}");
                return [];
            }
        }
    }

    public IReadOnlyList<ConnectionAuditEntry> ReadRecentSuccessfulConnections(int limit = 10)
    {
        lock (_gate)
        {
            try
            {
                var maximum = Math.Clamp(limit, 1, MaximumEntries);
                return GetEntriesUnsafe()
                    .Where(entry => entry.EventType == ConnectionAuditEventType.Connected)
                    .GroupBy(entry => entry.SessionId)
                    .Select(group => group.First())
                    .Take(maximum)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CxShell recent connection read failed: {ex.Message}");
                return [];
            }
        }
    }

    public void Record(
        SessionInfo session,
        ConnectionAuditEventType eventType,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entry = new ConnectionAuditEntry
        {
            SessionId = session.Id,
            SessionName = session.Name ?? string.Empty,
            Protocol = session.Protocol,
            Host = session.Host ?? string.Empty,
            Port = session.Port,
            Username = session.Username ?? string.Empty,
            EventType = eventType,
            Detail = TrimDetail(detail)
        };

        lock (_gate)
        {
            try
            {
                var entries = GetEntriesUnsafe();
                entries.Insert(0, entry);
                if (entries.Count > MaximumEntries)
                    entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);

                SaveUnsafe(entries);
            }
            catch (Exception ex)
            {
                // An audit write must never make a connection fail.
                Debug.WriteLine($"CxShell audit write failed: {ex.Message}");
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CxShell audit clear failed: {ex.Message}");
            }
        }
    }

    public void Export(string path, IReadOnlyList<ConnectionAuditEntry>? entries = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var exportEntries = entries ?? ReadRecent();
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(exportEntries, options);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private List<ConnectionAuditEntry> LoadUnsafe()
    {
        if (!File.Exists(_path))
            return [];

        var json = File.ReadAllText(_path, Encoding.UTF8);
        return JsonSerializer.Deserialize<List<ConnectionAuditEntry>>(json) ?? [];
    }

    private List<ConnectionAuditEntry> GetEntriesUnsafe()
    {
        return _entries ??= LoadUnsafe()
            .Take(MaximumEntries)
            .ToList();
    }

    private void SaveUnsafe(IReadOnlyList<ConnectionAuditEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(entries, options);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string TrimDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        var normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }

    private static string ResolveStorageDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData) && Path.IsPathFullyQualified(appData))
            return Path.Combine(appData, "CxShell");

        return Path.Combine(AppContext.BaseDirectory, ".cxshell-data");
    }
}
