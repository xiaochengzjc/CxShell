using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace CxShell.Models;

public enum AgentProviderType
{
    OpenAiChatCompatible,
    OpenAiResponses
}

/// <summary>
/// Model-level settings. Provider URL and credentials stay on the provider;
/// models only describe the selectable model and optional wire overrides.
/// </summary>
public sealed class AgentModelSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentProviderType? ProtocolOverride { get; set; }

    public bool Enabled { get; set; } = true;
    public int? MaxOutputTokens { get; set; }
}

/// <summary>
/// Global Agent provider configuration. EncryptedApiKey is encrypted at rest;
/// it must never be copied into an Agent snapshot or audit entry.
/// </summary>
public sealed class AgentProviderSettings
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BuiltinId { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentProviderType Type { get; set; } = AgentProviderType.OpenAiChatCompatible;

    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ActiveModelId { get; set; } = string.Empty;
    public List<AgentModelSettings> Models { get; set; } = [];
    public List<string> AvailableModels { get; set; } = [];
    public string EncryptedApiKey { get; set; } = string.Empty;
    public bool RequiresApiKey { get; set; } = true;
    public bool AllowInsecureTls { get; set; }
    private int _requestTimeoutSeconds = 300;

    public int RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set => _requestTimeoutSeconds = Math.Clamp(value, 5, 600);
    }
}
