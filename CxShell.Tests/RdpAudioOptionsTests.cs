using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpAudioOptionsTests
{
    [Theory]
    [InlineData("PlayLocal", RdpAudioPlaybackMode.PlayLocal)]
    [InlineData(" playremote ", RdpAudioPlaybackMode.PlayRemote)]
    [InlineData("DoNotPlay", RdpAudioPlaybackMode.DoNotPlay)]
    [InlineData(null, RdpAudioPlaybackMode.DoNotPlay)]
    [InlineData("legacy-value", RdpAudioPlaybackMode.DoNotPlay)]
    public void ResolvePlaybackMode_NormalizesKnownValues(
        string? value,
        RdpAudioPlaybackMode expected)
    {
        Assert.Equal(expected, RdpAudioOptions.ResolvePlaybackMode(value));
    }

    [Theory]
    [InlineData(RdpAudioPlaybackMode.DoNotPlay, false, false)]
    [InlineData(RdpAudioPlaybackMode.DoNotPlay, true, true)]
    [InlineData(RdpAudioPlaybackMode.PlayLocal, false, true)]
    [InlineData(RdpAudioPlaybackMode.PlayRemote, false, true)]
    public void RequiresNativeConfiguration_MatchesRequestedFeatures(
        RdpAudioPlaybackMode playbackMode,
        bool microphoneEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            RdpAudioOptions.RequiresNativeConfiguration(playbackMode, microphoneEnabled));
    }
}
