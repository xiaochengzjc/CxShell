using System.Text;
using System.Text.Json;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Services;

/// <summary>
/// Persists settings that belong to the application rather than to a session.
/// The optional fallback is used once to migrate settings from sessions.json.
/// </summary>
public sealed class ApplicationSettingsStore
{
    private const string FileName = "application-settings.json";
    private static readonly JsonSerializerOptions ComparisonJsonOptions = new();
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
            var beforeNormalization = JsonSerializer.Serialize(loaded, ComparisonJsonOptions);
            Normalize(loaded);
            var afterNormalization = JsonSerializer.Serialize(loaded, ComparisonJsonOptions);
            if (!hasSchemaVersion || !string.Equals(
                    beforeNormalization,
                    afterNormalization,
                    StringComparison.Ordinal))
                TrySave(loaded);

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
        var hasLegacySchema = settings.SchemaVersion < ApplicationSettings.CurrentSchemaVersion;
        if (settings.SchemaVersion < ApplicationSettings.CurrentSchemaVersion)
            settings.SchemaVersion = ApplicationSettings.CurrentSchemaVersion;

        settings.AgentProvider ??= new AgentProviderSettings();
        settings.AgentProvider.Name = settings.AgentProvider.Name?.Trim() ?? string.Empty;
        settings.AgentProvider.BuiltinId = settings.AgentProvider.BuiltinId?.Trim() ?? string.Empty;
        settings.AgentProvider.BaseUrl = settings.AgentProvider.BaseUrl?.Trim() ?? string.Empty;
        settings.AgentProvider.Model = settings.AgentProvider.Model?.Trim() ?? string.Empty;
        settings.AgentProvider.ActiveModelId = settings.AgentProvider.ActiveModelId?.Trim() ?? string.Empty;
        settings.AgentProvider.EncryptedApiKey = settings.AgentProvider.EncryptedApiKey?.Trim() ?? string.Empty;
        settings.AgentProvider.Models = (settings.AgentProvider.Models ?? [])
            .Where(model => model != null)
            .ToList();
        foreach (var model in settings.AgentProvider.Models)
        {
            model.Id = model.Id?.Trim() ?? string.Empty;
            model.Name = model.Name?.Trim() ?? string.Empty;
            model.ModelId = model.ModelId?.Trim() ?? string.Empty;
            if (model.MaxOutputTokens is <= 0)
                model.MaxOutputTokens = null;
        }
        settings.AgentWeb ??= new AgentWebSettings();
        settings.AgentWeb.Normalize();
        AgentProviderConfiguration.EnsureActiveModel(settings.AgentProvider);
        settings.AgentProvider.AvailableModels = (settings.AgentProvider.AvailableModels ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        settings.AgentPermissionMode = AgentPermissionPolicy.NormalizePermissionMode(
            settings.AgentPermissionMode);
        if (string.IsNullOrEmpty(settings.AgentPermissionMode))
            settings.AgentPermissionMode = AgentPermissionPolicy.RiskBasedApprovalMode;
        if (hasLegacySchema && settings.AgentProvider.RequestTimeoutSeconds == 120)
            settings.AgentProvider.RequestTimeoutSeconds = 300;
        settings.ThemeMode = settings.ThemeMode is ApplicationSettings.LightThemeMode
            ? ApplicationSettings.LightThemeMode
            : ApplicationSettings.DarkThemeMode;
        settings.UiLanguage = string.IsNullOrWhiteSpace(settings.UiLanguage)
            ? "zh-CN"
            : settings.UiLanguage.Trim();
        settings.SftpPanelWidth = NormalizeWidth(settings.SftpPanelWidth, 240, 800, 318);
        settings.AgentPanelWidth = NormalizeWidth(settings.AgentPanelWidth, 280, 600, 360);
        if (settings.AgentProvider.BaseUrl.Contains("/plan/v1", StringComparison.OrdinalIgnoreCase))
        {
            settings.AgentProvider.Type = AgentProviderType.OpenAiResponses;
            if (string.IsNullOrWhiteSpace(settings.AgentProvider.BuiltinId))
                settings.AgentProvider.BuiltinId = "routin-ai-plan";
        }
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

    private static double NormalizeWidth(double value, double minimum, double maximum, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Clamp(value, minimum, maximum);
}
