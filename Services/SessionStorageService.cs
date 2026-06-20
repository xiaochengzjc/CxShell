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
    private readonly string _storageDir;
    private readonly string _storagePath;

    public SessionStorageService()
    {
        _storageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChiXueSsh");
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
}
