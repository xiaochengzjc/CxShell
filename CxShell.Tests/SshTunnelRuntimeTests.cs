using CxShell.Services;
using CxShell.Models;

namespace CxShell.Tests;

public sealed class SshTunnelRuntimeTests
{
    [Fact]
    public void DisconnectedService_HasNoRuntimeEntries()
    {
        var service = new SshConnectionService();

        Assert.Empty(service.GetTunnelRuntimeSnapshot());
    }

    [Fact]
    public void StartTunnel_WhenDisconnected_ReturnsFailure()
    {
        var service = new SshConnectionService();

        var result = service.StartTunnel(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal("The SSH connection is not active.", result.ErrorMessage);
    }

    [Fact]
    public void StopTunnel_IsIdempotentAndPublishesRuntimeChange()
    {
        var service = new SshConnectionService();
        var notificationCount = 0;
        service.TunnelRuntimeChanged += () => notificationCount++;

        var first = service.StopTunnel(Guid.NewGuid());
        var second = service.StopTunnel(Guid.NewGuid());

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, notificationCount);
    }

    [Fact]
    public void TunnelActivity_RecordsConnectionsAndLatestOriginator()
    {
        var firstActivity = new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);
        var secondActivity = firstActivity.AddSeconds(5);

        var activity = SshTunnelActivitySnapshot.Empty
            .RecordConnection(firstActivity, "127.0.0.1:51000")
            .RecordConnection(secondActivity, "10.0.0.8:52000");

        Assert.Equal(2, activity.ConnectionCount);
        Assert.Equal(secondActivity, activity.LastActivityAt);
        Assert.Equal("10.0.0.8:52000", activity.LastOriginator);
    }

    [Fact]
    public void TunnelActivity_BlankOriginatorIsNotExposed()
    {
        var activity = SshTunnelActivitySnapshot.Empty.RecordConnection(
            DateTimeOffset.UtcNow,
            "   ");

        Assert.Equal(1, activity.ConnectionCount);
        Assert.Null(activity.LastOriginator);
    }

    [Fact]
    public void SessionInfo_RestoresConfiguredTunnelsByDefault()
    {
        var session = new CxShell.Models.SessionInfo();

        Assert.True(session.SshAutoRestoreTunnels);
    }
}
