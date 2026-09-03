using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

/// <summary>
/// Stable result contract shared by Agent tools. Tool-specific fields remain
/// at the JSON root so existing consumers can keep reading their specialized
/// payloads, while the common fields make success and retry decisions safe.
/// </summary>
public sealed record AgentToolResultEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool Success { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ExecutionState { get; init; } = string.Empty;
    public bool OutcomeCertain { get; init; }
    public bool RemoteCompletionConfirmed { get; init; }
    public string ErrorType { get; init; } = nameof(AgentCommandErrorType.None);
    public string Message { get; init; } = string.Empty;
    public string? Output { get; init; }
    public string? Error { get; init; }
    public int? ExitCode { get; init; }
    public long DurationMs { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public AgentCommandVerification Verification { get; init; } =
        new(AgentCommandVerificationState.Unknown, "The tool result requires additional verification.", false, null);
    public bool RetrySafe { get; init; }

    public static AgentToolResultEnvelope FromCommand(
        AgentCommandResult result,
        string sessionId,
        string? output = null,
        string? error = null,
        string? message = null,
        long? durationMs = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new()
        {
            Success = result.IsSuccess,
            Status = result.Status.ToString(),
            ExecutionState = result.ExecutionState.ToString(),
            OutcomeCertain = result.IsOutcomeCertain,
            RemoteCompletionConfirmed = result.RemoteCompletionConfirmed,
            ErrorType = result.ErrorType.ToString(),
            Message = message ?? result.Message,
            Output = output,
            Error = error,
            ExitCode = result.ExitCode,
            DurationMs = durationMs ?? result.DurationMs,
            RequestId = result.RequestId.ToString("D"),
            SessionId = sessionId,
            Verification = AgentCommandVerificationService.Evaluate(result),
            RetrySafe = result.IsRetrySafe
        };
    }

    public static string Serialize(
        AgentToolResultEnvelope result,
        params (string Name, object? Value)[] toolSpecificFields)
    {
        ArgumentNullException.ThrowIfNull(result);

        var node = JsonSerializer.SerializeToNode(result, JsonOptions) as JsonObject ?? new JsonObject();
        foreach (var (name, value) in toolSpecificFields)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            node[name] = value == null ? null : JsonSerializer.SerializeToNode(value, JsonOptions);
        }

        return node.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// Adds the common contract to an older or tool-specific JSON payload.
    /// Existing root fields win, which keeps specialized consumers compatible.
    /// </summary>
    public static string Merge(
        string content,
        bool success,
        string sessionId,
        long durationMs)
    {
        JsonObject node;
        var parsedObject = true;
        try
        {
            node = JsonNode.Parse(content) as JsonObject ?? new JsonObject();
            parsedObject = node.Count > 0 || string.IsNullOrWhiteSpace(content);
        }
        catch (JsonException)
        {
            node = new JsonObject();
            parsedObject = false;
        }

        SetIfMissing(node, "success", success);
        SetIfMissing(node, "status", success ? "completed" : "failed");
        SetIfMissing(node, "executionState", success ? "Completed" : "Failed");
        SetIfMissing(node, "outcomeCertain", success);
        SetIfMissing(node, "remoteCompletionConfirmed", false);
        SetIfMissing(node, "errorType", success ? nameof(AgentCommandErrorType.None) : nameof(AgentCommandErrorType.Unknown));
        var message = success ? "The tool call completed." : ExtractMessage(content);
        SetIfMissing(node, "message", message);
        if (!parsedObject && !string.IsNullOrWhiteSpace(content))
            SetIfMissing(node, "error", content);
        SetIfMissing(node, "durationMs", Math.Max(0, durationMs));
        SetIfMissing(node, "sessionId", sessionId);
        // A generic tool failure is not automatically safe to repeat. Tools that
        // can prove idempotency must opt in by returning retrySafe explicitly.
        SetIfMissing(node, "retrySafe", false);
        if (!node.ContainsKey("verification"))
        {
            node["verification"] = JsonSerializer.SerializeToNode(
                new AgentCommandVerification(
                    success ? AgentCommandVerificationState.Unknown : AgentCommandVerificationState.Failed,
                    success
                        ? "The tool call completed; remote completion was not confirmed."
                        : "The tool call failed.",
                    false,
                    null),
                JsonOptions);
        }

        return node.ToJsonString(JsonOptions);
    }

    private static void SetIfMissing(JsonObject node, string name, object? value)
    {
        if (!node.ContainsKey(name))
            node[name] = value == null ? null : JsonSerializer.SerializeToNode(value, JsonOptions);
    }

    private static string ExtractMessage(string content)
    {
        try
        {
            var node = JsonNode.Parse(content) as JsonObject;
            return node?["message"]?.GetValue<string>() ?? "The tool call failed.";
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(content) ? "The tool call failed." : content;
        }
    }
}
