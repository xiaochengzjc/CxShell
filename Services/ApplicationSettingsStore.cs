using System.Text.Json;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Services;

/// <summary>
/// Persists application settings in the shared SQLite database. The optional
/// fallback migrates settings embedded in the legacy sessions file.
/// </summary>
public sealed class ApplicationSettingsStore
{
    private const string LegacyFileName = "application-settings.json";
    private static readonly JsonSerializerOptions ComparisonJsonOptions = new();
    private readonly string _directory;
    private readonly string _path;
    private readonly SqliteAppDataStore _store;

    public ApplicationSettingsStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? SessionStorageService.GetStorageDirectory()
            : Path.GetFullPath(directory);
        _path = Path.Combine(_directory, LegacyFileName);
        _store = new SqliteAppDataStore(_directory);
    }

    public ApplicationSettings Load(ApplicationSettings? fallback = null)
    {
        var payload = _store.Read("application_settings");
        if (payload == null)
        {
            _store.ImportLegacyFile(
                "application_settings",
                "default",
                _path,
                static json => TryDeserialize(json) != null);
            payload = _store.Read("application_settings");
        }

        if (payload == null)
        {
            var migrated = fallback ?? new ApplicationSettings();
            Normalize(migrated);
            TrySave(migrated);
            return migrated;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ApplicationSettings>(payload);
            if (loaded == null)
                return Recover(fallback);

            using var document = JsonDocument.Parse(payload);
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

        Normalize(settings);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(settings, options);
        _store.Write("application_settings", "default", json);
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
        settings.GlobalProxy ??= new ProxySettings();
        NormalizeGlobalProxy(settings.GlobalProxy);
        settings.TrustedExternalLaunchTargets = (settings.TrustedExternalLaunchTargets ?? [])
            .Select(target => target?.Trim() ?? string.Empty)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
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

    private static void NormalizeGlobalProxy(ProxySettings proxy)
    {
        proxy.Host = proxy.Host?.Trim() ?? string.Empty;
        proxy.Username = proxy.Username?.Trim() ?? string.Empty;
        proxy.Password = proxy.Password?.Trim() ?? string.Empty;
        if (proxy.Protocol is ProxyProtocol.None or ProxyProtocol.JumpHost or ProxyProtocol.SshPassthrough)
        {
            proxy.Protocol = ProxyProtocol.None;
            proxy.Port = 0;
        }

        if (proxy.Port is < 1 or > 65535)
            proxy.Port = 0;
    }

    private static ApplicationSettings? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ApplicationSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
