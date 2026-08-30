using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentCommandTimeoutPolicyTests
{
    [Theory]
    [InlineData("apt-get install openjdk-21-jre")]
    [InlineData("sudo dnf upgrade java-21-openjdk")]
    [InlineData("winget install Microsoft.OpenJDK.21")]
    [InlineData("msiexec.exe /i runtime.msi")]
    [InlineData("npm install typescript")]
    public void InstallationAndUpgradeCommandsAreLongRunning(string command)
    {
        Assert.True(AgentCommandTimeoutPolicy.IsLongRunning(command));
    }

    [Fact]
    public void ShortModelTimeoutIsRaisedForExplicitLongRunningCommand()
    {
        var timeout = AgentCommandTimeoutPolicy.Resolve(
            "apt install openjdk-21-jre",
            TimeSpan.FromSeconds(120),
            hasExplicitTimeout: true);

        Assert.Equal(AgentCommandTimeoutPolicy.LongRunningMinimumTimeout, timeout);
    }

    [Fact]
    public void MissingTimeoutUsesLongRunningDefault()
    {
        var timeout = AgentCommandTimeoutPolicy.Resolve(
            "sudo apt-get update",
            AgentCommandTimeoutPolicy.DefaultTimeout,
            hasExplicitTimeout: false);

        Assert.Equal(AgentCommandTimeoutPolicy.LongRunningDefaultTimeout, timeout);
    }

    [Fact]
    public void RegularCommandHonorsItsRequestedTimeoutWithinBounds()
    {
        var timeout = AgentCommandTimeoutPolicy.Resolve(
            "java --version",
            TimeSpan.FromSeconds(120),
            hasExplicitTimeout: true);

        Assert.Equal(TimeSpan.FromSeconds(120), timeout);
    }

    [Fact]
    public async Task LongRunningGatewayRequestCanStillBeCancelled()
    {
        var snapshot = AgentSessionSnapshot.FromSession(
            new SessionInfo
            {
                Name = "Timeout policy test",
                Host = "timeout-policy.example",
                Protocol = SessionProtocol.SSH
            },
            isConnected: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    async (_, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return string.Empty;
                    })
            ]));
        using var cancellation = new CancellationTokenSource();

        var execution = gateway.ExecuteCommandAsync(
            new AgentCommandRequest
            {
                SessionId = snapshot.SessionId,
                Command = "apt-get install openjdk-21-jre",
                Timeout = TimeSpan.FromSeconds(120)
            },
            cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentCommandStatus.Cancelled, result.Status);
    }
}
