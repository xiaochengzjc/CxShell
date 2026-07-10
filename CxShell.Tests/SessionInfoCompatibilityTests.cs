using System.Text.Json;
using CxShell.Models;

namespace CxShell.Tests;

public sealed class SessionInfoCompatibilityTests
{
    [Fact]
    public void NewSession_UsesConservativePanelAndRemoteTitleDefaults()
    {
        var session = new SessionInfo();

        Assert.False(session.SshAutoOpenSftpPanel);
        Assert.False(session.SshAutoOpenMonitorPanel);
        Assert.False(session.TerminalAdvancedAllowTitleChange);
        Assert.False(session.RdpMicrophoneEnabled);
    }

    [Fact]
    public void LegacyDisableTitleSetting_DoesNotOptIntoRemoteTabTitles()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"TerminalAdvancedDisableTitleChange":false}""");

        Assert.NotNull(session);
        Assert.False(session.TerminalAdvancedAllowTitleChange);
    }

    [Fact]
    public void ExplicitAllowTitleSetting_RoundTrips()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"TerminalAdvancedAllowTitleChange":true}""");

        Assert.NotNull(session);
        Assert.True(session.TerminalAdvancedAllowTitleChange);
    }

    [Fact]
    public void LegacyAudioCaptureSetting_DoesNotEnableMicrophoneRedirection()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"RdpAudioCapture":true}""");

        Assert.NotNull(session);
        Assert.True(session.RdpAudioCapture);
        Assert.False(session.RdpMicrophoneEnabled);
    }
}
