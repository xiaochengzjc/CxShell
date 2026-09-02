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
    public bool RequiresApiKey { get; init; }
    public bool HasApiKey { get; init; }
    public bool AllowInsecureTls { get; init; }
    public int RequestTimeoutSeconds { get; init; }
    public AgentProviderCapabilities Capabilities { get; init; } = new();
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

        if (string.IsNullOrWhiteSpace(settings.Model))
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
            Model = settings.Model ?? string.Empty,
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

    public static bool IsResponsesProvider(AgentProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Type == AgentProviderType.OpenAiResponses ||
               settings.BaseUrl.Contains("/plan/v1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalDevelopmentHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
