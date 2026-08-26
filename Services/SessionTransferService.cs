using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

public static class SessionTransferService
{
    public const string StorageFormat = "CxShell.Session";
    public const string Format = "CxShell.SessionExport";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Export(string path, IEnumerable<SessionGroup> groups, IEnumerable<SessionInfo> sessions)
    {
        var selectedSessions = sessions.ToList();
        var selectedIds = selectedSessions
            .Select(session => session.GroupId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        var sourceGroups = groups.ToDictionary(group => group.Id);
        var exportGroups = new Dictionary<Guid, SessionGroup>();

        foreach (var groupId in selectedIds)
            AddGroupAndParents(groupId, sourceGroups, exportGroups);

        var package = new SessionData
        {
            Format = Format,
            Version = "1.0",
            ExportedAt = DateTime.Now,
            // Global application settings are intentionally not part of a session package.
            Settings = null,
            Groups = exportGroups.Values
                .OrderBy(group => group.SortOrder)
                .Select(CloneGroup)
                .ToList(),
            Sessions = selectedSessions
                .Select(CloneSessionForExport)
                .ToList(),
            QuickSessionIds = new List<Guid>()
        };

        var json = JsonSerializer.Serialize(package, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static SessionData Load(string path)
    {
        var json = File.ReadAllText(path);

        var package = JsonSerializer.Deserialize<SessionData>(json)
                      ?? throw new InvalidDataException("The session export file is empty.");

        if ((!string.Equals(package.Format, Format, StringComparison.Ordinal) &&
             !string.Equals(package.Format, StorageFormat, StringComparison.Ordinal)) ||
            !string.Equals(package.Version, "1.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The session export file format is not supported.");
        }

        package.Groups ??= new List<SessionGroup>();
        package.Sessions ??= new List<SessionInfo>();
        if (package.Sessions.Count == 0)
            throw new InvalidDataException("The session export file contains no sessions.");

        return package;
    }

    public static SessionInfo CloneImportedSession(SessionInfo source, Guid? groupId, string name, int sortOrder)
    {
        var clone = CloneSessionForExport(source);
        clone.Id = Guid.NewGuid();
        clone.Name = name;
        clone.GroupId = groupId;
        clone.SortOrder = sortOrder;
        clone.CreatedAt = DateTime.Now;
        return clone;
    }

    public static SessionGroup CloneImportedGroup(SessionGroup source, Guid id, Guid? parentId, string name, int sortOrder)
    {
        return new SessionGroup
        {
            Id = id,
            Name = name,
            ParentId = parentId,
            SortOrder = sortOrder
        };
    }

    private static void AddGroupAndParents(
        Guid groupId,
        IReadOnlyDictionary<Guid, SessionGroup> sourceGroups,
        IDictionary<Guid, SessionGroup> result)
    {
        if (result.ContainsKey(groupId) || !sourceGroups.TryGetValue(groupId, out var group))
            return;

        if (group.ParentId.HasValue)
            AddGroupAndParents(group.ParentId.Value, sourceGroups, result);

        result[groupId] = group;
    }

    private static SessionGroup CloneGroup(SessionGroup source)
    {
        return new SessionGroup
        {
            Id = source.Id,
            Name = source.Name,
            ParentId = source.ParentId,
            SortOrder = source.SortOrder
        };
    }

    private static SessionInfo CloneSessionForExport(SessionInfo source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var clone = JsonSerializer.Deserialize<SessionInfo>(json)
                    ?? throw new InvalidDataException("Failed to copy a session for export.");

        clone.Password = KeepEncryptedOrEmpty(clone.Password);
        clone.PrivateKeyPassphrase = KeepEncryptedOrEmpty(clone.PrivateKeyPassphrase);
        clone.RdpSshPassword = KeepEncryptedOrEmpty(clone.RdpSshPassword);
        clone.VncSshPassword = KeepEncryptedOrEmpty(clone.VncSshPassword);
        clone.Proxy = CloneProxyForExport(clone.Proxy);
        clone.ProxyServers = clone.ProxyServers.Select(CloneProxyForExport).ToList();
        return clone;
    }

    private static ProxySettings CloneProxyForExport(ProxySettings? source)
    {
        source ??= new ProxySettings();
        var clone = new ProxySettings
        {
            Id = source.Id,
            Name = source.Name,
            Protocol = source.Protocol,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = KeepEncryptedOrEmpty(source.Password),
            AuthMethod = source.AuthMethod,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = KeepEncryptedOrEmpty(source.PrivateKeyPassphrase),
            UseAgent = source.UseAgent,
            UseSessionFile = source.UseSessionFile,
            SessionFilePath = source.SessionFilePath,
            NextProxyId = source.NextProxyId
        };
        return clone;
    }

    private static string KeepEncryptedOrEmpty(string? value)
    {
        return PasswordEncryptionService.IsEncryptedValue(value) ? value! : string.Empty;
    }
}
