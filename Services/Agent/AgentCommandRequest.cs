using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

public sealed record AgentCommandRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public string Command { get; init; } = string.Empty;
    public string? DisplayCommand { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool AppendLineEnding { get; init; } = true;
    public string? ApprovalToken { get; init; }

    /// <summary>
    /// Internal, process-local grant used when an already approved command is
    /// retried with sensitive input. It is never accepted from the runtime
    /// protocol or serialized into a request.
    /// </summary>
    internal bool ApprovalGranted { get; init; }

    /// <summary>
    /// Sensitive input for an interactive command. It is process-local only;
    /// never serialize, log, or include it in an Agent message.
    /// </summary>
    [JsonIgnore]
    public string? SensitiveInput { get; init; }

    /// <summary>
    /// Original command used for permission evaluation after an approved
    /// command is rewritten to accept sensitive input. Process-local only.
    /// </summary>
    [JsonIgnore]
    internal string? ApprovedCommand { get; init; }
}
