using System.Text.Json.Serialization;

namespace CxShell.Models;

public enum AgentProviderType
{
    OpenAiChatCompatible,
    OpenAiResponses
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
