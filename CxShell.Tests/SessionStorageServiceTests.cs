using System.Text.Json;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SessionStorageServiceTests
{
    [Fact]
    public void Save_DoesNotDuplicateApplicationSettings()
    {
        using var directory = new TemporaryDirectory();
        var service = new SessionStorageService(directory.Path);
        service.Save(new SessionData
        {
            Settings = new ApplicationSettings { ThemeMode = ApplicationSettings.LightThemeMode },
            Sessions =
            [
                new SessionInfo { Name = "server", Host = "127.0.0.1" }
            ]
        });

        var json = File.ReadAllText(Path.Combine(directory.Path, "sessions.json"));
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("Settings", out _));
        Assert.Contains("server", json);
    }

    [Fact]
    public void Load_ReadsLegacyApplicationSettingsForMigration()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sessions.json");
        File.WriteAllText(path, """
            {
              "Format": "CxShell.Session",
              "Version": "1.0",
              "Settings": { "ThemeMode": "Light", "ShowTabBar": true },
              "Groups": [],
              "Sessions": []
            }
            """);

        var data = new SessionStorageService(directory.Path).Load();

        Assert.NotNull(data.Settings);
        Assert.Equal(ApplicationSettings.LightThemeMode, data.Settings!.ThemeMode);
        Assert.True(data.Settings.ShowTabBar);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cxshell-session-storage-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test artifacts.
            }
        }
    }
}
