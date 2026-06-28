using System.Collections.Generic;
using System.Text;
using CxShell.Models;

namespace CxShell.Services;

public class SessionData
{
    public string Version { get; set; } = "1.0";
    public ApplicationSettings Settings { get; set; } = new();
    public List<SessionGroup> Groups { get; set; } = new();
    public List<SessionInfo> Sessions { get; set; } = new();
    public List<Guid> QuickSessionIds { get; set; } = new();
}

public class SessionStorageService
{
    private const string CurrentAppDirectoryName = "CxShell";

    private readonly string _storageDir;
    private readonly string _storagePath;

    public SessionStorageService()
    {
        _storageDir = ResolveStorageDirectory();
        _storagePath = Path.Combine(_storageDir, "sessions.json");
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

        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        };
        var json = System.Text.Json.JsonSerializer.Serialize(data, options);
        File.WriteAllText(_storagePath, json, Encoding.UTF8);
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
