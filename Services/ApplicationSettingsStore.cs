using System.Text;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

/// <summary>
/// Persists settings that belong to the application rather than to a session.
/// The optional fallback is used once to migrate settings from sessions.json.
/// </summary>
public sealed class ApplicationSettingsStore
{
    private const string FileName = "application-settings.json";
    private readonly string _directory;
    private readonly string _path;

    public ApplicationSettingsStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? SessionStorageService.GetStorageDirectory()
            : Path.GetFullPath(directory);
        _path = Path.Combine(_directory, FileName);
    }

    public ApplicationSettings Load(ApplicationSettings? fallback = null)
    {
        if (!File.Exists(_path))
        {
            var migrated = fallback ?? new ApplicationSettings();
            Normalize(migrated);
            TrySave(migrated);
            return migrated;
        }

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<ApplicationSettings>(json);
            if (loaded == null)
                return Recover(fallback);

            using var document = JsonDocument.Parse(json);
            var hasSchemaVersion = document.RootElement
                .TryGetProperty(nameof(ApplicationSettings.SchemaVersion), out _);
            if (!hasSchemaVersion || loaded.SchemaVersion < ApplicationSettings.CurrentSchemaVersion)
            {
                Normalize(loaded);
                TrySave(loaded);
            }

            return loaded;
        }
        catch (JsonException)
        {
            return Recover(fallback);
        }
        catch (IOException)
        {
            return fallback ?? new ApplicationSettings();
        }
    }

    public void Save(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(_directory);
        Normalize(settings);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(settings, options);
        var temporaryPath = Path.Combine(
            _directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private ApplicationSettings Recover(ApplicationSettings? fallback)
    {
        var recovered = fallback ?? new ApplicationSettings();
        Normalize(recovered);
        TrySave(recovered);
        return recovered;
    }

    private static void Normalize(ApplicationSettings settings)
    {
        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = ApplicationSettings.CurrentSchemaVersion;
    }

    private void TrySave(ApplicationSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch (IOException)
        {
            // Settings are best-effort during startup; keep the in-memory values usable.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only profile must not prevent the application from starting.
        }
    }
}
