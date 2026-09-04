using CxShell.Models;

namespace CxShell.Services.Agent;

public enum AgentProviderValidationStatus
{
    Valid,
    Disabled,
    MissingBaseUrl,
    InvalidBaseUrl,
    InsecureBaseUrl,
    MissingModel,
    MissingApiKey,
    UnsupportedProvider
}

public sealed record AgentProviderValidationResult(
    AgentProviderValidationStatus Status,
    string Message)
{
    public bool IsValid => Status == AgentProviderValidationStatus.Valid;
}

public sealed record AgentProviderSnapshot
{
    public bool Enabled { get; init; }
    public string Name { get; init; } = string.Empty;
    public string BuiltinId { get; init; } = string.Empty;
    public AgentProviderType Type { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ActiveModelId { get; init; } = string.Empty;
    public IReadOnlyList<AgentModelSnapshot> Models { get; init; } = [];
    public IReadOnlyList<string> AvailableModels { get; init; } = [];
    public bool RequiresApiKey { get; init; }
    public bool HasApiKey { get; init; }
    public bool AllowInsecureTls { get; init; }
    public int RequestTimeoutSeconds { get; init; }
    public AgentProviderCapabilities Capabilities { get; init; } = new();
}

public sealed record AgentModelSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public AgentProviderType? ProtocolOverride { get; init; }
    public bool Enabled { get; init; }
    public int? MaxOutputTokens { get; init; }
}

public sealed record AgentProviderCapabilities
{
    public bool SupportsTools { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsDocumentInput { get; init; }
    public bool SupportsResponsesApi { get; init; }
    public bool SupportsTokenUsage { get; init; }
    public bool SupportsReasoning { get; init; }
}

public static class AgentProviderConfiguration
{
    public static AgentProviderValidationResult Validate(AgentProviderSettings? settings)
    {
        if (settings == null || !settings.Enabled)
            return new(AgentProviderValidationStatus.Disabled, "Agent provider is disabled.");

        if (settings.Type is not (AgentProviderType.OpenAiChatCompatible or AgentProviderType.OpenAiResponses))
            return new(AgentProviderValidationStatus.UnsupportedProvider, "The provider type is not supported yet.");

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            return new(AgentProviderValidationStatus.MissingBaseUrl, "Provider base URL is required.");

        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return new(AgentProviderValidationStatus.InvalidBaseUrl, "Provider base URL must be an HTTP(S) URL without credentials.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !settings.AllowInsecureTls && !IsLocalDevelopmentHost(uri.Host))
            return new(AgentProviderValidationStatus.InsecureBaseUrl, "HTTPS is required for a non-local provider.");

        if (string.IsNullOrWhiteSpace(GetEffectiveModelId(settings)))
            return new(AgentProviderValidationStatus.MissingModel, "Provider model is required.");

        if (settings.RequiresApiKey && string.IsNullOrWhiteSpace(GetApiKey(settings)))
            return new(AgentProviderValidationStatus.MissingApiKey, "Provider API key is not configured.");

        return new(AgentProviderValidationStatus.Valid, "Provider is ready.");
    }

    public static AgentProviderSnapshot ToSnapshot(AgentProviderSettings? settings)
    {
        settings ??= new AgentProviderSettings();
        return new AgentProviderSnapshot
        {
            Enabled = settings.Enabled,
            Name = settings.Name ?? string.Empty,
            BuiltinId = settings.BuiltinId ?? string.Empty,
            Type = settings.Type,
            BaseUrl = settings.BaseUrl ?? string.Empty,
            Model = GetEffectiveModelId(settings),
            ActiveModelId = settings.ActiveModelId ?? string.Empty,
            Models = settings.Models
                .Where(model => model != null && !string.IsNullOrWhiteSpace(model.ModelId))
                .Select(model => new AgentModelSnapshot
                {
                    Id = model.Id ?? string.Empty,
                    Name = model.Name ?? string.Empty,
                    ModelId = model.ModelId ?? string.Empty,
                    ProtocolOverride = model.ProtocolOverride,
                    Enabled = model.Enabled,
                    MaxOutputTokens = model.MaxOutputTokens
                })
                .ToArray(),
            AvailableModels = settings.AvailableModels?.ToArray() ?? [],
            RequiresApiKey = settings.RequiresApiKey,
            HasApiKey = !string.IsNullOrWhiteSpace(GetApiKey(settings)),
            AllowInsecureTls = settings.AllowInsecureTls,
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            Capabilities = GetCapabilities(settings)
        };
    }

    public static AgentProviderCapabilities GetCapabilities(AgentProviderSettings? settings)
    {
        if (settings == null || !settings.Enabled ||
            settings.Type is not (AgentProviderType.OpenAiChatCompatible or AgentProviderType.OpenAiResponses))
            return new();

        return new AgentProviderCapabilities
        {
            SupportsTools = true,
            SupportsStreaming = true,
            // Both supported wire formats can carry OpenAI-compatible image
            // input. Document attachments are extracted to text by CxShell.
            SupportsVision = true,
            SupportsDocumentInput = true,
            SupportsResponsesApi = settings.Type == AgentProviderType.OpenAiResponses,
            SupportsTokenUsage = true,
            SupportsReasoning = settings.Type == AgentProviderType.OpenAiResponses
        };
    }

    public static string GetApiKey(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PasswordEncryptionService.DecryptEncrypted(settings.EncryptedApiKey);
    }

    public static string GetEffectiveModelId(
        AgentProviderSettings settings,
        string? requestedModel = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(requestedModel))
            return requestedModel.Trim();

        var selected = settings.Models?.FirstOrDefault(model =>
            model.Enabled &&
            string.Equals(model.Id, settings.ActiveModelId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model.ModelId));
        if (!string.IsNullOrWhiteSpace(selected?.ModelId))
            return selected.ModelId.Trim();

        if (!string.IsNullOrWhiteSpace(settings.Model))
            return settings.Model.Trim();

        return settings.Models?
                   .FirstOrDefault(model => model.Enabled && !string.IsNullOrWhiteSpace(model.ModelId))?
                   .ModelId?.Trim() ?? string.Empty;
    }

    public static AgentModelSettings EnsureActiveModel(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Models ??= [];
        var active = settings.Models.FirstOrDefault(model =>
            model.Enabled &&
            !string.IsNullOrWhiteSpace(model.ModelId) &&
            string.Equals(model.Id, settings.ActiveModelId, StringComparison.OrdinalIgnoreCase));
        if (active != null)
            return active;

        active = settings.Models.FirstOrDefault(model =>
            model.Enabled && !string.IsNullOrWhiteSpace(model.ModelId));
        if (active != null)
        {
            settings.ActiveModelId = active.Id;
            return active;
        }

        var effectiveModel = settings.Model?.Trim() ?? string.Empty;

        active = new AgentModelSettings
        {
            Name = effectiveModel,
            ModelId = effectiveModel,
            Enabled = true
        };
        settings.Models.Insert(0, active);
        settings.ActiveModelId = active.Id;
        return active;
    }

    public static IReadOnlyList<string> ApplyModelCatalog(
        AgentProviderSettings settings,
        IEnumerable<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(modelIds);

        var ids = modelIds
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        settings.AvailableModels = ids.ToList();
        settings.Models ??= [];

        foreach (var modelId in ids)
        {
            var existing = settings.Models.FirstOrDefault(model =>
                string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                settings.Models.Add(new AgentModelSettings
                {
                    Name = modelId,
                    ModelId = modelId,
                    Enabled = true
                });
            }
        }

        if (string.IsNullOrWhiteSpace(settings.Model) && ids.Length > 0)
            settings.Model = ids[0];
        EnsureActiveModel(settings);
        return ids;
    }

    public static void SetApiKey(AgentProviderSettings settings, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EncryptedApiKey = PasswordEncryptionService.Encrypt(apiKey?.Trim());
    }

    public static Uri BuildChatCompletionsUri(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(baseUrl, UriKind.Absolute);

        return new Uri(baseUrl + "/chat/completions", UriKind.Absolute);
    }

    public static Uri BuildResponsesUri(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return new Uri(baseUrl, UriKind.Absolute);

        return new Uri(baseUrl + "/responses", UriKind.Absolute);
    }

    public static Uri BuildModelsUri(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/responses", "/models" })
        {
            if (baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl[..^suffix.Length];
        }

        return new Uri(baseUrl + "/models", UriKind.Absolute);
    }

    public static bool IsResponsesProvider(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsResponsesProvider(settings, null);
    }

    public static bool IsResponsesProvider(
        AgentProviderSettings settings,
        string? requestedModel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var model = !string.IsNullOrWhiteSpace(requestedModel)
            ? settings.Models?.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.ModelId, requestedModel, StringComparison.OrdinalIgnoreCase))
            : settings.Models?.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.Id, settings.ActiveModelId, StringComparison.OrdinalIgnoreCase));
        var type = model?.ProtocolOverride ?? settings.Type;
        return type == AgentProviderType.OpenAiResponses ||
               settings.BaseUrl.Contains("/plan/v1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalDevelopmentHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
