using System.Text.Json;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class ApplicationSettingsStoreTests
{
    [Fact]
    public void Load_MigratesFallbackSettingsWhenStandaloneFileIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var fallback = new ApplicationSettings
        {
            ThemeMode = ApplicationSettings.LightThemeMode,
            ShowTabBar = true,
            SftpPanelWidth = 420
        };

        var loaded = new ApplicationSettingsStore(directory.Path).Load(fallback);

        Assert.Same(fallback, loaded);
        var saved = JsonSerializer.Deserialize<ApplicationSettings>(
            File.ReadAllText(Path.Combine(directory.Path, "application-settings.json")));
        Assert.NotNull(saved);
        Assert.Equal(ApplicationSettings.LightThemeMode, saved.ThemeMode);
        Assert.True(saved.ShowTabBar);
        Assert.Equal(420, saved.SftpPanelWidth);
    }

    [Fact]
    public void Load_PrefersStandaloneSettingsOverSessionFallback()
    {
        using var directory = new TemporaryDirectory();
        var store = new ApplicationSettingsStore(directory.Path);
        store.Save(new ApplicationSettings
        {
            ThemeMode = ApplicationSettings.LightThemeMode,
            ShowTabBar = true
        });

        var loaded = store.Load(new ApplicationSettings
        {
            ThemeMode = ApplicationSettings.DarkThemeMode,
            ShowTabBar = false
        });

        Assert.Equal(ApplicationSettings.LightThemeMode, loaded.ThemeMode);
        Assert.True(loaded.ShowTabBar);
    }

    [Fact]
    public void Load_UsesFallbackWhenStandaloneSettingsAreInvalid()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "application-settings.json");
        File.WriteAllText(path, "{ invalid json");
        var fallback = new ApplicationSettings { ShowTabBar = true };

        var loaded = new ApplicationSettingsStore(directory.Path).Load(fallback);

        Assert.Same(fallback, loaded);
        Assert.True(loaded.ShowTabBar);
    }

    [Fact]
    public void Save_WritesTheCurrentSchemaVersion()
    {
        using var directory = new TemporaryDirectory();
        var settings = new ApplicationSettings { SchemaVersion = 0 };

        new ApplicationSettingsStore(directory.Path).Save(settings);

        Assert.Equal(ApplicationSettings.CurrentSchemaVersion, settings.SchemaVersion);
        var json = File.ReadAllText(Path.Combine(directory.Path, "application-settings.json"));
        Assert.Contains("\"SchemaVersion\": 1", json);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cxshell-settings-tests-{Guid.NewGuid():N}");
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
