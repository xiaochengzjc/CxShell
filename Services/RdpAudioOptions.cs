using System;

namespace CxShell.Services;

public enum RdpAudioPlaybackMode
{
    PlayLocal = 0,
    PlayRemote = 1,
    DoNotPlay = 2
}

public static class RdpAudioOptions
{
    public static RdpAudioPlaybackMode ResolvePlaybackMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "playlocal" => RdpAudioPlaybackMode.PlayLocal,
            "playremote" => RdpAudioPlaybackMode.PlayRemote,
            _ => RdpAudioPlaybackMode.DoNotPlay
        };
    }

    public static bool RequiresNativeConfiguration(
        RdpAudioPlaybackMode playbackMode,
        bool microphoneEnabled)
    {
        return playbackMode != RdpAudioPlaybackMode.DoNotPlay || microphoneEnabled;
    }
}
