namespace CxShell.Services.Agent;

public static class AgentContextEstimator
{
    public const int CharactersPerEstimatedToken = 4;

    public static AgentContextEstimate Estimate(IReadOnlyList<AgentChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var characters = 0L;
        foreach (var message in messages)
        {
            if (message == null)
                continue;

            characters += message.Content?.Length ?? 0;
            characters += message.Role?.Length ?? 0;
            characters += message.ToolCallId?.Length ?? 0;
            characters += message.ToolName?.Length ?? 0;
            characters += message.ToolArguments?.Length ?? 0;
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    characters += toolCall.Id.Length + toolCall.Name.Length + toolCall.Arguments.Length;
                }
            }

            if (message.ContentParts is { Count: > 0 })
            {
                foreach (var part in message.ContentParts)
                {
                    characters += part.Text?.Length ?? 0;
                    // Image bytes are not tokens, but the part has request
                    // overhead. Keep the estimate deliberately conservative.
                    characters += string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase)
                        ? 256
                        : 16;
                }
            }

            characters += 32;
        }

        var boundedCharacters = Math.Min(int.MaxValue, characters);
        var estimatedTokens = (int)Math.Min(
            int.MaxValue,
            (characters + CharactersPerEstimatedToken - 1) / CharactersPerEstimatedToken);
        return new AgentContextEstimate(
            messages.Count,
            (int)boundedCharacters,
            estimatedTokens);
    }
}
