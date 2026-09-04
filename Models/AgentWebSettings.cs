namespace CxShell.Models;

/// <summary>
/// Global network access settings for the Agent. Web access is deliberately
/// opt-in because requests may contain operational context and can reach
/// resources on the user's network.
/// </summary>
public sealed class AgentWebSettings
{
    public bool Enabled { get; set; }
    public string SearxngBaseUrl { get; set; } = string.Empty;
    public bool AllowPrivateNetwork { get; set; }
    public string AllowedPrivateHosts { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 5;
    public int MaxFetchCharacters { get; set; } = 60_000;
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    public void Normalize()
    {
        MaxResults = Math.Clamp(MaxResults, 1, 20);
        MaxFetchCharacters = Math.Clamp(MaxFetchCharacters, 2_000, 400_000);
        MaxResponseBytes = Math.Clamp(MaxResponseBytes, 16 * 1024, 8 * 1024 * 1024);
        SearxngBaseUrl = SearxngBaseUrl?.Trim() ?? string.Empty;
        AllowedPrivateHosts = AllowedPrivateHosts?.Trim() ?? string.Empty;
    }
}
