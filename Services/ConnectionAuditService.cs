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
    private readonly SqliteAppDataStore _store;
    private List<ConnectionAuditEntry>? _entries;

    public ConnectionAuditService(string? storagePath = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(ResolveStorageDirectory(), "connection-audit.json")
            : Path.GetFullPath(storagePath);
        _store = new SqliteAppDataStore(Path.GetDirectoryName(_path));
    }

    public string StoragePath => _store.DatabasePath;

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
            Detail = TrimDetail(detail),
            Source = "manual"
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

    public void RecordExternalLaunch(
        ExternalLaunchRequest request,
        string result,
        Guid? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entry = new ConnectionAuditEntry
        {
            SessionId = sessionId ?? Guid.NewGuid(),
            SessionName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? request.TargetText
                : request.DisplayName,
            Protocol = request.Protocol,
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            EventType = ConnectionAuditEventType.ExternalLaunch,
            Detail = TrimDetail(result),
            Source = "external",
            ExternalOrigin = request.Origin.ToString(),
            CredentialSupplied = request.HasCredential
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
                Debug.WriteLine($"CxShell external audit write failed: {ex.Message}");
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
                _store.Delete("connection_audit", "entries");
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
        var payload = _store.Read("connection_audit", "entries");
        if (payload == null)
        {
            _store.ImportLegacyFile(
                "connection_audit",
                "entries",
                _path,
                static json => TryDeserialize(json) != null);
            payload = _store.Read("connection_audit", "entries");
        }

        return payload == null ? [] : TryDeserialize(payload) ?? [];
    }

    private List<ConnectionAuditEntry> GetEntriesUnsafe()
    {
        return _entries ??= LoadUnsafe()
            .Take(MaximumEntries)
            .ToList();
    }

    private void SaveUnsafe(IReadOnlyList<ConnectionAuditEntry> entries)
    {
        _store.Write("connection_audit", "entries", JsonSerializer.Serialize(entries));
    }

    private static List<ConnectionAuditEntry>? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ConnectionAuditEntry>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
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
