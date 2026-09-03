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

    [Fact]
    public void DispatchedCommandIsNotAnOutcomeCertainResult()
    {
        var result = new AgentCommandResult
        {
            Status = AgentCommandStatus.Sent,
            ExecutionState = AgentCommandExecutionState.Dispatched,
            RemoteCompletionConfirmed = false
        };

        Assert.False(result.IsOutcomeCertain);
        Assert.Equal(AgentCommandVerificationState.Unknown,
            AgentCommandVerificationService.Evaluate(result).State);
    }

    [Fact]
    public void TimeoutAndCancellationRemainUncertain()
    {
        var timeout = new AgentCommandResult
        {
            Status = AgentCommandStatus.TimedOut,
            ExecutionState = AgentCommandExecutionState.Unknown,
            ErrorType = AgentCommandErrorType.Timeout
        };
        var cancelled = new AgentCommandResult
        {
            Status = AgentCommandStatus.Cancelled,
            ExecutionState = AgentCommandExecutionState.Cancelled,
            ErrorType = AgentCommandErrorType.Cancelled
        };

        Assert.False(timeout.IsOutcomeCertain);
        Assert.False(cancelled.IsOutcomeCertain);
        Assert.True(timeout.TimedOut);
        Assert.True(cancelled.WasCancelled);
    }

    [Fact]
    public void CompletedAndConfirmedFailureAreTheOnlyCertainOutcomes()
    {
        var completed = new AgentCommandResult
        {
            Status = AgentCommandStatus.Sent,
            ExecutionState = AgentCommandExecutionState.Completed,
            RemoteCompletionConfirmed = true
        };
        var failed = new AgentCommandResult
        {
            Status = AgentCommandStatus.Failed,
            ExecutionState = AgentCommandExecutionState.Failed,
            RemoteCompletionConfirmed = true
        };
        var unconfirmedFailure = failed with { RemoteCompletionConfirmed = false };

        Assert.True(completed.IsOutcomeCertain);
        Assert.True(failed.IsOutcomeCertain);
        Assert.False(unconfirmedFailure.IsOutcomeCertain);
    }
}
