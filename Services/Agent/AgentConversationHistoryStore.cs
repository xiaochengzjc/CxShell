using System.Text.Json;
using System.Text.Json.Serialization;
using CxShell.Services;

namespace CxShell.Services.Agent;

/// <summary>
/// A user-visible Agent conversation. A conversation contains the messages
/// shown in the panel, while individual runs and tool calls are represented
/// as nested message records.
/// </summary>
public sealed record AgentConversationHistoryRecord(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("sessionId")] string? SessionId = null,
    [property: JsonPropertyName("sessionLabel")] string? SessionLabel = null,
    [property: JsonPropertyName("mode")] AgentChatMode Mode = AgentChatMode.Agent,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("contextMessages")] IReadOnlyList<AgentChatMessage>? ContextMessages = null,
    [property: JsonPropertyName("messages")] IReadOnlyList<AgentConversationMessageRecord>? Messages = null,
    [property: JsonPropertyName("isPinned")] bool IsPinned = false,
    [property: JsonPropertyName("isArchived")] bool IsArchived = false);

public sealed record AgentConversationAnnotationRecord(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceLabel")] string? SourceLabel = null);

/// <summary>
/// Serializable presentation data for one visible Agent message. Sensitive
/// credential values are intentionally not part of this record.
/// </summary>
public sealed record AgentConversationMessageRecord(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("runId")] string? RunId = null,
    [property: JsonPropertyName("statusText")] string? StatusText = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("toolInput")] string? ToolInput = null,
    [property: JsonPropertyName("durationText")] string? DurationText = null,
    [property: JsonPropertyName("riskText")] string? RiskText = null,
    [property: JsonPropertyName("approvalSessionText")] string? ApprovalSessionText = null,
    [property: JsonPropertyName("approvalTimeoutText")] string? ApprovalTimeoutText = null,
    [property: JsonPropertyName("verificationStatus")] string? VerificationStatus = null,
    [property: JsonPropertyName("verificationText")] string? VerificationText = null,
    [property: JsonPropertyName("summarySessionName")] string? SummarySessionName = null,
    [property: JsonPropertyName("summaryStatusText")] string? SummaryStatusText = null,
    [property: JsonPropertyName("summaryDurationText")] string? SummaryDurationText = null,
    [property: JsonPropertyName("summaryToolCallCount")] int SummaryToolCallCount = 0,
    [property: JsonPropertyName("summaryModelRequestCount")] int SummaryModelRequestCount = 0,
    [property: JsonPropertyName("summaryResultText")] string? SummaryResultText = null,
    [property: JsonPropertyName("contentParts")] IReadOnlyList<AgentContentPart>? ContentParts = null,
    [property: JsonPropertyName("annotations")] IReadOnlyList<AgentConversationAnnotationRecord>? Annotations = null,
    [property: JsonPropertyName("children")] IReadOnlyList<AgentConversationMessageRecord>? Children = null);

public interface IAgentConversationHistoryStore
{
    IReadOnlyList<AgentConversationHistoryRecord> Load();
    void Save(AgentConversationHistoryRecord conversation);
    void Delete(string conversationId);
    void Clear();
}

/// <summary>
/// Reads the encrypted conversation file used by older versions. New history
/// is always written through <see cref="SqliteAgentConversationHistoryStore"/>.
/// </summary>
internal static class LegacyAgentConversationHistoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool TryLoad(
        string filePath,
        out IReadOnlyList<AgentConversationHistoryRecord> conversations)
    {
        conversations = [];
        try
        {
            if (!File.Exists(filePath))
                return false;
            var stored = File.ReadAllText(filePath);
            var json = PasswordEncryptionService.Decrypt(stored);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var parsed = JsonSerializer.Deserialize<List<AgentConversationHistoryRecord>>(json, JsonOptions);
            if (parsed == null)
                return false;

            conversations = parsed
                .Where(IsValid)
                .OrderByDescending(item => item.IsPinned)
                .ThenByDescending(item => item.UpdatedAtUtc)
                .ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValid(AgentConversationHistoryRecord? conversation)
        => conversation != null &&
           Guid.TryParse(conversation.ConversationId, out _) &&
           !string.IsNullOrWhiteSpace(conversation.Title) &&
           conversation.Title.Length <= 160 &&
           conversation.Messages is { Count: > 0 } &&
           conversation.Messages.Count <= 1000;
}

internal sealed class NullAgentConversationHistoryStore : IAgentConversationHistoryStore
{
    public IReadOnlyList<AgentConversationHistoryRecord> Load() => [];
    public void Save(AgentConversationHistoryRecord conversation)
    {
    }

    public void Delete(string conversationId)
    {
    }

    public void Clear()
    {
    }
}
