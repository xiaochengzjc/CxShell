using System;
using System.Threading.Tasks;
using CxShell.Models;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class SessionLifecycleTests
{
    [Fact]
    public async Task DisposedSftpDocumentDoesNotStartAnotherConnection()
    {
        using var sftp = new SftpViewModel();
        sftp.Dispose();

        var session = new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "lifecycle-test",
            Host = "127.0.0.1",
            Port = 22,
            Username = "test",
            Protocol = SessionProtocol.SFTP
        };

        Assert.True(sftp.IsDisposed);
        Assert.False(await sftp.SwitchConnectionAsync(session, null));
    }

    [Fact]
    public void TabDisposeIsIdempotentAndDisposesCompanionPanels()
    {
        var session = new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "lifecycle-test",
            Host = "127.0.0.1",
            Port = 22,
            Username = "test",
            Protocol = SessionProtocol.SSH
        };
        var tab = new TerminalTabViewModel(session);

        tab.Dispose();
        tab.Dispose();

        Assert.True(tab.IsDisposed);
        Assert.True(tab.CompanionSftp.IsDisposed);
    }

    [Fact]
    public void MonitorDisposeIsIdempotent()
    {
        var monitor = new ServerMonitorViewModel();

        monitor.Dispose();
        monitor.Dispose();

        Assert.True(monitor.IsDisposed);
    }
}
