using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using CxShell.Models;

namespace CxShell.Services;

public class SessionData
{
    public string Format { get; set; } = "CxShell.Session";
    public string Version { get; set; } = "1.0";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApplicationSettings? Settings { get; set; } = new();
    public List<SessionGroup> Groups { get; set; } = new();
    public List<SessionInfo> Sessions { get; set; } = new();
    public List<Guid> QuickSessionIds { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExportedAt { get; set; }
}

public class SessionStorageService
{
    private const string CurrentAppDirectoryName = "CxShell";

    private readonly string _storageDir;
    private readonly string _storagePath;

    public SessionStorageService(string? directory = null)
    {
        _storageDir = string.IsNullOrWhiteSpace(directory)
            ? GetStorageDirectory()
            : Path.GetFullPath(directory);
        _storagePath = Path.Combine(_storageDir, "sessions.json");
    }

    public static string GetStorageDirectory()
    {
        return ResolveStorageDirectory();
    }

    public SessionData Load()
    {
        if (!File.Exists(_storagePath))
        {
            return new SessionData();
        }

        var json = File.ReadAllText(_storagePath, Encoding.UTF8);
        return System.Text.Json.JsonSerializer.Deserialize<SessionData>(json)
               ?? new SessionData();
    }

    public void Save(SessionData data)
    {
        if (!Directory.Exists(_storageDir))
        {
            Directory.CreateDirectory(_storageDir);
        }

        var persisted = new SessionData
        {
            Format = data.Format,
            Version = data.Version,
            // Application settings have their own file. Keep loading this field
            // above for migration, but do not duplicate it in new session files.
            Settings = null,
            Groups = data.Groups,
            Sessions = data.Sessions,
            QuickSessionIds = data.QuickSessionIds,
            ExportedAt = data.ExportedAt
        };
        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        };
        var json = System.Text.Json.JsonSerializer.Serialize(persisted, options);
        var temporaryPath = Path.Combine(
            _storageDir,
            $".sessions.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ResolveStorageDirectory()
    {
        var root = ResolveStorageRoot();
        if (!string.IsNullOrWhiteSpace(root))
            return Path.Combine(root, CurrentAppDirectoryName);

        return Path.Combine(AppContext.BaseDirectory, ".cxshell-data");
    }

    private static string? ResolveStorageRoot()
    {
        var appData = TryGetFullRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (!string.IsNullOrWhiteSpace(appData))
            return appData;

        if (OperatingSystem.IsWindows())
            return null;

        var xdgConfigHome = TryGetFullRoot(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return xdgConfigHome;

        var userProfile = TryGetFullRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return OperatingSystem.IsMacOS()
                ? Path.Combine(userProfile, "Library", "Application Support")
                : Path.Combine(userProfile, ".config");
        }

        return null;
    }

    private static string? TryGetFullRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(root);
            if (!Path.IsPathFullyQualified(expanded))
                return null;

            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }
}
