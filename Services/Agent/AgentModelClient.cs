using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services.Agent;

public sealed record AgentChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,
    IReadOnlyList<AgentContentPart>? ContentParts = null);

/// <summary>
/// Provider-neutral content attached to a chat message. Images are sent as
/// base64 data URLs; document parts contain bounded extracted text so the
/// model receives the document contents rather than a local file path.
/// </summary>
public sealed record AgentContentPart(
    string Type,
    string? Text = null,
    string? MediaType = null,
    string? Data = null,
    string? FileName = null)
{
    public static AgentContentPart TextPart(string text, string? fileName = null)
        => new("text", Text: text, FileName: fileName);

    public static AgentContentPart ImagePart(
        string mediaType,
        string base64Data,
        string? fileName = null)
        => new("image", MediaType: mediaType, Data: base64Data, FileName: fileName);
}

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters);

public sealed record AgentToolCall(
    string Id,
    string Name,
    string Arguments);

public sealed record AgentModelRequest(
    IReadOnlyList<AgentChatMessage> Messages,
    string? Model = null,
    double? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<AgentToolDefinition>? Tools = null);

public sealed record AgentModelResponse(
    string Text,
    string Model,
    string Provider,
    int? InputTokens = null,
    int? OutputTokens = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null);

public sealed record AgentModelStreamChunk(
    string Text = "",
    string? Thinking = null);

public enum AgentProviderErrorKind
{
    Network,
    Timeout,
    Authentication,
    RateLimited,
    Server,
    Protocol,
    Request
}

public sealed class AgentProviderException : InvalidOperationException
{
    public AgentProviderException(
        AgentProviderErrorKind kind,
        string safeMessage,
        bool retryable,
        int? statusCode = null,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Kind = kind;
        SafeMessage = safeMessage;
        Retryable = retryable;
        StatusCode = statusCode;
    }

    public AgentProviderErrorKind Kind { get; }
    public string SafeMessage { get; }
    public bool Retryable { get; }
    public int? StatusCode { get; }

    public static AgentProviderException FromStatusCode(
        int statusCode,
        string? endpoint = null)
    {
        var endpointText = string.IsNullOrWhiteSpace(endpoint)
            ? "Provider request"
            : $"Provider request to '{SafeEndpoint(endpoint)}'";
        var (kind, retryable, reason) = statusCode switch
        {
            401 or 403 => (AgentProviderErrorKind.Authentication, false, "authentication failed"),
            408 => (AgentProviderErrorKind.Timeout, true, "timed out"),
            429 => (AgentProviderErrorKind.RateLimited, true, "was rate limited"),
            >= 500 and <= 599 => (AgentProviderErrorKind.Server, true, "returned a server error"),
            >= 400 and <= 499 => (AgentProviderErrorKind.Request, false, "was rejected"),
            _ => (AgentProviderErrorKind.Request, false, "returned an unexpected status")
        };
        return new(
            kind,
            $"{endpointText} {reason} (HTTP {statusCode}).",
            retryable,
            statusCode);
    }

    public static AgentProviderException Network(Exception exception, string? endpoint = null)
    {
        var endpointText = string.IsNullOrWhiteSpace(endpoint)
            ? "Provider request"
            : $"Provider request to '{SafeEndpoint(endpoint)}'";
        return new(
            AgentProviderErrorKind.Network,
            $"{endpointText} failed because the network connection was unavailable.",
            retryable: true,
            innerException: exception);
    }

    public static AgentProviderException Timeout(string? endpoint = null, Exception? innerException = null)
    {
        var endpointText = string.IsNullOrWhiteSpace(endpoint)
            ? "Provider request"
            : $"Provider request to '{SafeEndpoint(endpoint)}'";
        return new(
            AgentProviderErrorKind.Timeout,
            $"{endpointText} timed out.",
            retryable: true,
            innerException: innerException);
    }

    public static AgentProviderException Protocol(string message, Exception? innerException = null)
        => new(AgentProviderErrorKind.Protocol, message, retryable: false, innerException: innerException);

    private static string SafeEndpoint(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Path);

        return "configured endpoint";
    }
}

public interface IAgentModelClient
{
    Task<AgentModelResponse> CompleteAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAgentStreamingModelClient
{
    Task<AgentModelResponse> CompleteStreamingAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        Action<AgentModelStreamChunk> onChunk,
        CancellationToken cancellationToken = default);
}
