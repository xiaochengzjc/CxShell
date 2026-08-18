using System.Text.Json;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SessionInfoCompatibilityTests
{
    [Fact]
    public void NewSession_UsesConservativePanelAndRemoteTitleDefaults()
    {
        var session = new SessionInfo();

        Assert.False(session.SshAutoOpenSftpPanel);
        Assert.False(session.SshAutoOpenMonitorPanel);
        Assert.True(session.SshEnableServerMonitoring);
        Assert.False(session.SshEnableMonitorNetworkLatency);
        Assert.False(session.TerminalAdvancedAllowTitleChange);
        Assert.False(session.TerminalAdvancedAllowOsc52Clipboard);
        Assert.False(session.RdpMicrophoneEnabled);
        Assert.Equal(SessionTabIconCatalog.Default, session.AppearanceTabIcon);
    }

    [Fact]
    public void SessionTabIconCatalog_NormalizesKnownAndUnknownValues()
    {
        Assert.Equal(SessionTabIconCatalog.Server, SessionTabIconCatalog.Normalize("server"));
        Assert.Equal(SessionTabIconCatalog.Default, SessionTabIconCatalog.Normalize("not-a-tab-icon"));
        Assert.Equal(SessionTabIconCatalog.Default, SessionTabIconCatalog.Normalize(null));
    }

    [Fact]
    public void SessionInfo_DeserializesTabIconSetting()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"AppearanceTabIcon":"Database"}""");

        Assert.NotNull(session);
        Assert.Equal(SessionTabIconCatalog.Database, session.AppearanceTabIcon);
    }

    [Fact]
    public void ApplicationSettings_UsesTheDefaultSftpPanelWidth()
    {
        var settings = new ApplicationSettings();

        Assert.Equal(318, settings.SftpPanelWidth);
        Assert.False(settings.ShowTabBar);
    }

    [Fact]
    public void SessionInfo_UsesAndClampsServerMonitorDefaults()
    {
        var session = new SessionInfo
        {
            SshMonitorRefreshIntervalSeconds = 0
        };

        Assert.True(session.SshEnableServerMonitoring);
        Assert.Equal(SessionInfo.MinSshMonitorRefreshIntervalSeconds,
            session.SshMonitorRefreshIntervalSeconds);

        session.SshMonitorRefreshIntervalSeconds = 100;

        Assert.Equal(SessionInfo.MaxSshMonitorRefreshIntervalSeconds,
            session.SshMonitorRefreshIntervalSeconds);
    }

    [Fact]
    public void SessionInfo_DeserializesMonitorSettings()
    {
        var session = JsonSerializer.Deserialize<SessionInfo>(
            """{"SshEnableServerMonitoring":false,"SshEnableMonitorNetworkLatency":true,"SshMonitorRefreshIntervalSeconds":10}""");

        Assert.NotNull(session);
        Assert.False(session.SshEnableServerMonitoring);
        Assert.True(session.SshEnableMonitorNetworkLatency);
        Assert.Equal(10, session.SshMonitorRefreshIntervalSeconds);
    }

    [Fact]
    public void ApplicationSettings_PreservesTabBarVisibility()
    {
        var settings = JsonSerializer.Deserialize<ApplicationSettings>(
            """{"ShowTabBar":true}""");

        Assert.NotNull(settings);
        Assert.True(settings.ShowTabBar);
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
