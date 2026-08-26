using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class ServerMonitorViewModelTests
{
    [Fact]
    public void SetSuspendedChangesLifecycleStateImmediately()
    {
        var monitor = new ServerMonitorViewModel();
        try
        {
            monitor.SetSuspended(true);
            Assert.True(monitor.IsSuspended);

            monitor.SetSuspended(false);
            Assert.False(monitor.IsSuspended);
        }
        finally
        {
            monitor.Dispose();
        }
    }
}
