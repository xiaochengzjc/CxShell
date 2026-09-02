using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentCommandVerificationTests
{
    [Fact]
    public void CompletedRemoteCommandIsVerified()
    {
        var verification = AgentCommandVerificationService.Evaluate(new AgentCommandResult
        {
            Status = AgentCommandStatus.Sent,
            ExecutionState = AgentCommandExecutionState.Completed,
            RemoteCompletionConfirmed = true,
            ExitCode = 0
        });

        Assert.Equal(AgentCommandVerificationState.Verified, verification.State);
        Assert.True(verification.RemoteCompletionConfirmed);
    }

    [Fact]
    public void FailedRemoteCommandIsFailed()
    {
        var verification = AgentCommandVerificationService.Evaluate(new AgentCommandResult
        {
            Status = AgentCommandStatus.Failed,
            ExecutionState = AgentCommandExecutionState.Failed,
            RemoteCompletionConfirmed = true,
            ExitCode = 1
        });

        Assert.Equal(AgentCommandVerificationState.Failed, verification.State);
        Assert.Contains("1", verification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueuedCommandHasUnknownVerification()
    {
        var verification = AgentCommandVerificationService.Evaluate(new AgentCommandResult
        {
            Status = AgentCommandStatus.Sent,
            ExecutionState = AgentCommandExecutionState.Dispatched,
            RemoteCompletionConfirmed = false
        });

        Assert.Equal(AgentCommandVerificationState.Unknown, verification.State);
        Assert.False(verification.RemoteCompletionConfirmed);
    }
}
