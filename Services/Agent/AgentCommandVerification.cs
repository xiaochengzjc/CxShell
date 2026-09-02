namespace CxShell.Services.Agent;

public enum AgentCommandVerificationState
{
    Verified,
    Failed,
    Unknown
}

public sealed record AgentCommandVerification(
    AgentCommandVerificationState State,
    string Message,
    bool RemoteCompletionConfirmed,
    int? ExitCode);

public static class AgentCommandVerificationService
{
    public static AgentCommandVerification Evaluate(AgentCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.RemoteCompletionConfirmed && result.IsSuccess)
        {
            return new(
                AgentCommandVerificationState.Verified,
                result.ExitCode is null or 0
                    ? "Remote completion was confirmed successfully."
                    : $"Remote completion was confirmed with exit code {result.ExitCode}.",
                true,
                result.ExitCode);
        }

        if (result.RemoteCompletionConfirmed)
        {
            return new(
                AgentCommandVerificationState.Failed,
                result.ExitCode is { } exitCode
                    ? $"Remote completion was confirmed as failed (exit code {exitCode})."
                    : "Remote completion was confirmed as failed.",
                true,
                result.ExitCode);
        }

        return new(
            AgentCommandVerificationState.Unknown,
            "The command was dispatched, but remote completion could not be confirmed.",
            false,
            result.ExitCode);
    }
}
