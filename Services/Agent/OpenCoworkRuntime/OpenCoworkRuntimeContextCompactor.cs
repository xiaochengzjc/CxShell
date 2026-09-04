using System.Text;
using CxShell.Services.Agent;

namespace CxShell.Services.Agent.OpenCoworkRuntime;

/// <summary>
/// Context compaction adapted from OpenCoWork's AgentRuntimeContextCompression.
/// It keeps the system prompt and a safe recent-message boundary, asks the host
/// for a provider summary when possible, and always has a local fallback.
/// </summary>
public sealed class OpenCoworkRuntimeContextCompactor
{
    public const int DefaultMessageLimit = 72;
    public const int DefaultCharacterLimit = 384 * 1024;
    public const int DefaultPreserveRecentMessages = 20;
    public const int SummaryTimeoutSeconds = 120;

    private const int MinimumMessagesToCompress = 2;
    private const int SummaryInputCharacterLimit = 192 * 1024;
    private const int SummaryMessageCharacterLimit = 8 * 1024;

    private readonly int _messageLimit;
    private readonly int _characterLimit;
    private readonly int _preserveRecentMessages;

    public OpenCoworkRuntimeContextCompactor(
        int messageLimit = DefaultMessageLimit,
        int characterLimit = DefaultCharacterLimit,
        int preserveRecentMessages = DefaultPreserveRecentMessages)
    {
        if (messageLimit < 4)
            throw new ArgumentOutOfRangeException(nameof(messageLimit));
        if (characterLimit < 8 * 1024)
            throw new ArgumentOutOfRangeException(nameof(characterLimit));
        if (preserveRecentMessages < 1 || preserveRecentMessages >= messageLimit)
            throw new ArgumentOutOfRangeException(nameof(preserveRecentMessages));

        _messageLimit = messageLimit;
        _characterLimit = characterLimit;
        _preserveRecentMessages = preserveRecentMessages;
    }

    public bool ShouldCompress(IReadOnlyList<AgentChatMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Count > _messageLimit ||
               EstimateCharacters(conversation) > _characterLimit;
    }

    public AgentContextEstimate Estimate(IReadOnlyList<AgentChatMessage> conversation)
        => AgentContextEstimator.Estimate(conversation);

    public async Task<OpenCoworkRuntimeContextCompressionResult> CompressIfNeededAsync(
        IReadOnlyList<AgentChatMessage> conversation,
        Func<IReadOnlyList<AgentChatMessage>, CancellationToken, Task<string?>>? summarizeAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!ShouldCompress(conversation))
            return OpenCoworkRuntimeContextCompressionResult.Unchanged(conversation);

        var prefixCount = CountLeadingSystemMessages(conversation);
        var preserveCount = Math.Min(
            _preserveRecentMessages,
            Math.Max(0, conversation.Count - prefixCount - MinimumMessagesToCompress));
        var boundary = FindSafeBoundary(
            conversation,
            Math.Max(prefixCount, conversation.Count - preserveCount),
            prefixCount);
        var messagesToCompress = conversation
            .Skip(prefixCount)
            .Take(Math.Max(0, boundary - prefixCount))
            .ToList();
        var messagesToPreserve = conversation.Skip(boundary).ToList();

        if (messagesToCompress.Count < MinimumMessagesToCompress)
            return OpenCoworkRuntimeContextCompressionResult.Unchanged(conversation);

        string? summary = null;
        var usedFallback = false;
        if (summarizeAsync != null)
        {
            try
            {
                summary = await summarizeAsync(messagesToCompress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A summarizer timeout should fall back to local compaction.
            }
            catch
            {
                // A provider or protocol failure must not terminate the live run.
            }
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = BuildLocalSummary(messagesToCompress);
            usedFallback = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compacted = new List<AgentChatMessage>(
            prefixCount + 2 + messagesToPreserve.Count);
        compacted.AddRange(conversation.Take(prefixCount));
        compacted.Add(new AgentChatMessage(
            "user",
            BuildBoundaryMessage(messagesToCompress.Count, messagesToPreserve.Count)));
        compacted.Add(new AgentChatMessage(
            "user",
            BuildSummaryMessage(summary, usedFallback)));
        compacted.AddRange(messagesToPreserve);

        return new OpenCoworkRuntimeContextCompressionResult(
            true,
            compacted,
            conversation.Count,
            compacted.Count,
            messagesToCompress.Count,
            usedFallback);
    }

    public static string BuildSummaryPrompt(IReadOnlyList<AgentChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var builder = new StringBuilder();
        builder.AppendLine("Conversation records to compress:");
        builder.AppendLine();
        foreach (var message in messages)
        {
            var entry = FormatMessage(message);
            if (builder.Length + entry.Length > SummaryInputCharacterLimit)
            {
                builder.AppendLine("[older records omitted from the summarizer input]");
                break;
            }

            builder.AppendLine(entry);
        }

        return builder.ToString();
    }

    private static int CountLeadingSystemMessages(IReadOnlyList<AgentChatMessage> conversation)
    {
        var count = 0;
        while (count < conversation.Count &&
               string.Equals(conversation[count].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static int FindSafeBoundary(
        IReadOnlyList<AgentChatMessage> conversation,
        int initialBoundary,
        int prefixCount)
    {
        var boundary = Math.Clamp(initialBoundary, prefixCount, conversation.Count);
        while (boundary > prefixCount &&
               boundary < conversation.Count &&
               string.Equals(conversation[boundary].Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            // Keep an assistant tool-call and every adjacent tool result
            // together. A single tool call may yield multiple result messages.
            boundary--;
        }

        return boundary;
    }



    private static int EstimateCharacters(IReadOnlyList<AgentChatMessage> conversation)
        => AgentContextEstimator.Estimate(conversation).CharacterCount;

    private static string BuildBoundaryMessage(int compressedCount, int preservedCount)
        => $"[Context Memory Boundary]\n{compressedCount} earlier messages were compressed " +
           $"into durable working memory. {preservedCount} recent messages remain verbatim. " +
           "Use the summary as context and continue from the latest verified state.";

    private static string BuildSummaryMessage(string summary, bool usedFallback)
    {
        var source = usedFallback
            ? "The provider summarizer was unavailable; this is a local safety summary."
            : "This summary was produced by the configured Agent provider.";
        return $"[Context Memory Compressed Summary]\n{source}\n\n{summary.Trim()}";
    }

    private static string BuildLocalSummary(IReadOnlyList<AgentChatMessage> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Local summary of the earlier Agent context:");

        var userMessages = messages
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(message => LimitExcerpt(message.Content, SummaryMessageCharacterLimit))
            .Where(text => text.Length > 0)
            .Take(4)
            .ToArray();
        if (userMessages.Length > 0)
        {
            builder.AppendLine("User requests:");
            foreach (var message in userMessages)
                builder.AppendLine($"- {message}");
        }

        var commands = messages
            .Where(message => message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!)
            .Select(call => $"{call.Name}: {LimitExcerpt(call.Arguments, 2 * 1024)}")
            .Take(12)
            .ToArray();
        if (commands.Length > 0)
        {
            builder.AppendLine("Commands and tools used:");
            foreach (var command in commands)
                builder.AppendLine($"- {command}");
        }

        var results = messages
            .Where(message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            .Select(message => LimitExcerpt(message.Content, SummaryMessageCharacterLimit))
            .Where(text => text.Length > 0)
            .TakeLast(4)
            .ToArray();
        if (results.Length > 0)
        {
            builder.AppendLine("Recent tool results:");
            foreach (var result in results)
                builder.AppendLine($"- {result}");
        }

        return builder.ToString().Trim();
    }

    private static string FormatMessage(AgentChatMessage message)
    {
        var builder = new StringBuilder();
        builder.Append("role=").AppendLine(message.Role);
        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            builder.Append("tool_call_id=").AppendLine(message.ToolCallId);
        if (!string.IsNullOrWhiteSpace(message.Content))
            builder.Append("content=").AppendLine(LimitExcerpt(message.Content, SummaryMessageCharacterLimit));
        if (message.ToolCalls is { Count: > 0 })
        {
            builder.AppendLine("tool_calls=");
            foreach (var call in message.ToolCalls)
            {
                builder.Append("- ")
                    .Append(call.Name)
                    .Append(": ")
                    .AppendLine(LimitExcerpt(call.Arguments, 2 * 1024));
            }
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string LimitExcerpt(string? value, int limit)
    {
        var text = AgentSensitiveDataRedactor.Redact(value?.Trim() ?? string.Empty);
        if (text.Length <= limit)
            return text;

        var prefixLength = limit / 2;
        var suffixLength = limit - prefixLength;
        return text[..prefixLength] +
               "\n...[excerpt truncated]...\n" +
               text[^suffixLength..];
    }
}

public sealed record OpenCoworkRuntimeContextCompressionResult(
    bool IsCompressed,
    IReadOnlyList<AgentChatMessage> Messages,
    int OriginalMessageCount,
    int NewMessageCount,
    int MessagesSummarized,
    bool UsedFallback)
{
    public static OpenCoworkRuntimeContextCompressionResult Unchanged(
        IReadOnlyList<AgentChatMessage> messages)
        => new(false, messages, messages.Count, messages.Count, 0, false);
}
