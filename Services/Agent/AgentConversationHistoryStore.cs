using System.Text;
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
/// Encrypted, bounded local history. The store is deliberately independent
/// from the runtime run store so the UI can restore full conversations even
/// after the runtime has pruned its event stream.
/// </summary>
public sealed class JsonAgentConversationHistoryStore : IAgentConversationHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _filePath;

    public JsonAgentConversationHistoryStore(string? filePath = null)
    {
        var useDefaultPath = string.IsNullOrWhiteSpace(filePath);
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "agent-history.json")
            : Path.GetFullPath(filePath);
        if (useDefaultPath)
            MigrateLegacyRunHistory();
    }

    public IReadOnlyList<AgentConversationHistoryRecord> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return [];

                var stored = File.ReadAllText(_filePath, Encoding.UTF8);
                var json = PasswordEncryptionService.Decrypt(stored);
                if (string.IsNullOrWhiteSpace(json))
                    return [];

                return JsonSerializer.Deserialize<List<AgentConversationHistoryRecord>>(json, JsonOptions)
                           ?.Where(IsValid)
                           .OrderByDescending(item => item.IsPinned)
                           .ThenByDescending(item => item.UpdatedAtUtc)
                           .ToArray()
                       ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Save(AgentConversationHistoryRecord conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!IsValid(conversation))
            return;

        lock (_gate)
        {
            var conversations = LoadUnsafe()
                .Where(item => !string.Equals(item.ConversationId, conversation.ConversationId, StringComparison.Ordinal))
                .Append(conversation)
                .OrderByDescending(item => item.IsPinned)
                .ThenByDescending(item => item.UpdatedAtUtc)
                .ToArray();
            WriteUnsafe(conversations);
        }
    }

    public void Delete(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        lock (_gate)
        {
            var conversations = LoadUnsafe()
                .Where(item => !string.Equals(item.ConversationId, conversationId, StringComparison.Ordinal))
                .ToArray();
            WriteUnsafe(conversations);
        }
    }

    public void Clear()
    {
        lock (_gate)
            WriteUnsafe([]);
    }

    private AgentConversationHistoryRecord[] LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_filePath))
                return [];

            var stored = File.ReadAllText(_filePath, Encoding.UTF8);
            var json = PasswordEncryptionService.Decrypt(stored);
            return string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<AgentConversationHistoryRecord>>(json, JsonOptions)
                      ?.Where(IsValid)
                      .ToArray()
                  ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyCollection<AgentConversationHistoryRecord> conversations)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(conversations, JsonOptions);
            var encrypted = PasswordEncryptionService.Encrypt(json);
            File.WriteAllText(temporaryPath, encrypted, new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private void MigrateLegacyRunHistory()
    {
        if (File.Exists(_filePath))
            return;

        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "agent-runs.json");
            var legacyRuns = new JsonAgentRunHistoryStore(legacyPath).Load();
            if (legacyRuns.Count == 0)
                return;

            var migrated = legacyRuns.Select(run =>
            {
                var prompt = string.IsNullOrWhiteSpace(run.PromptPreview)
                    ? "Agent run"
                    : run.PromptPreview!;
                var updatedAt = run.CompletedAtUtc ?? run.StartedAtUtc;
                var userMessage = new AgentConversationMessageRecord(
                    "user",
                    prompt,
                    run.StartedAtUtc,
                    RunId: run.RunId);
                var summaryMessage = new AgentConversationMessageRecord(
                    "summary",
                    string.Empty,
                    updatedAt,
                    RunId: run.RunId,
                    SummarySessionName: run.SessionId,
                    SummaryStatusText: run.Status,
                    SummaryDurationText: run.DurationMs is { } duration
                        ? $"{duration} ms"
                        : string.Empty,
                    SummaryToolCallCount: run.ToolCallCount,
                    SummaryModelRequestCount: run.ModelRequestCount,
                    SummaryResultText: run.Error ?? run.EndReason ?? run.Status);
                return new AgentConversationHistoryRecord(
                    Guid.NewGuid().ToString("D"),
                    prompt.Length <= 160 ? prompt : prompt[..160],
                    run.StartedAtUtc,
                    updatedAt,
                    run.SessionId,
                    run.SessionId,
                    run.Mode,
                    run.Provider,
                    run.Model,
                    [new AgentChatMessage("user", prompt)],
                    [userMessage, summaryMessage]);
            }).ToArray();
            WriteUnsafe(migrated);
        }
        catch
        {
            // A legacy history file is optional and must not block startup.
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
