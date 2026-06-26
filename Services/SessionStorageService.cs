using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

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
    private const string LegacyAppDirectoryName = "ChiXueSsh";

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
        foreach (var root in EnumerateStorageRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var fullRoot = TryGetFullRoot(root);
            if (string.IsNullOrWhiteSpace(fullRoot))
                continue;

            var current = Path.Combine(fullRoot, CurrentAppDirectoryName);
            var legacy = Path.Combine(fullRoot, LegacyAppDirectoryName);

            if (File.Exists(Path.Combine(current, "sessions.json")))
                return current;

            if (File.Exists(Path.Combine(legacy, "sessions.json")))
                return legacy;

            if (!File.Exists(current))
                return current;

            if (!File.Exists(legacy))
                return legacy;
        }

        return Path.Combine(AppContext.BaseDirectory, ".cxshell-data");
    }

    private static IEnumerable<string> EnumerateStorageRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdgConfigHome))
                yield return xdgConfigHome;
        }

        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            yield return OperatingSystem.IsWindows()
                ? Path.Combine(userProfile, "AppData", "Roaming")
                : Path.Combine(userProfile, ".config");
    }

    private static string? TryGetFullRoot(string root)
    {
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
