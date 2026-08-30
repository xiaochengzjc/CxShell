using CxShell.Services.Agent;

namespace CxShell.Services.Agent.OpenCoworkRuntime;

/// <summary>
/// The small, host-independent part of OpenCoWork's Agent runtime.
///
/// This is intentionally adapted to CxShell's model and tool contracts instead
/// of depending on the OpenCoWork Worker process. The loop owns conversation
/// progression and safety budgets; the host owns provider calls, tool policy,
/// UI events, and session access.
/// </summary>
public sealed class OpenCoworkRuntimeLoop
{
    private readonly OpenCoworkRuntimeLoopOptions _options;
    private readonly OpenCoworkRuntimeContextCompactor _contextCompactor;

    public OpenCoworkRuntimeLoop(OpenCoworkRuntimeLoopOptions? options = null)
    {
        _options = options ?? new OpenCoworkRuntimeLoopOptions();
        if (_options.MaximumModelRequests <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "A model request limit is required.");
        if (_options.MaximumToolCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "A tool call limit is required.");
        _contextCompactor = new OpenCoworkRuntimeContextCompactor(
            _options.ContextMessageLimit,
            _options.ContextCharacterLimit,
            _options.ContextPreserveRecentMessages);
    }

    public async Task<OpenCoworkRuntimeLoopResult> ExecuteAsync(
        List<AgentChatMessage> conversation,
        Func<int, IReadOnlyList<AgentChatMessage>, CancellationToken, Task<AgentModelResponse>> completeModelAsync,
        Func<AgentToolCall, CancellationToken, Task<OpenCoworkRuntimeToolResult>> executeToolAsync,
        Action<int>? modelRequestStarted = null,
        Action<AgentModelResponse>? modelResponseReceived = null,
        Action<AgentToolCall, OpenCoworkRuntimeToolResult>? toolCallCompleted = null,
        Func<IReadOnlyList<AgentChatMessage>, CancellationToken, Task<string?>>? summarizeContextAsync = null,
        Action<OpenCoworkRuntimeContextCompressionResult>? contextCompressed = null,
        Action<List<AgentChatMessage>>? applyPendingMessages = null,
        Func<bool>? stopRequested = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(completeModelAsync);
        ArgumentNullException.ThrowIfNull(executeToolAsync);

        var modelRequestCount = 0;
        var toolCallCount = 0;
        var hasIterationLimit = _options.MaximumIterations > 0;

        // This follows OpenCoWork's ExecuteLoopAsync shape: zero means an
        // unlimited model/tool turn loop, while independent budgets remain.
        for (var iteration = 1;
             !hasIterationLimit || iteration <= _options.MaximumIterations;
             iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopRequested?.Invoke() == true)
            {
                return new(
                    "stopped",
                    iteration - 1,
                    modelRequestCount,
                    toolCallCount);
            }
            applyPendingMessages?.Invoke(conversation);

            if (_options.EnableContextCompression && _contextCompactor.ShouldCompress(conversation))
            {
                var compression = await _contextCompactor.CompressIfNeededAsync(
                        conversation,
                        summarizeContextAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (compression.IsCompressed)
                {
                    conversation.Clear();
                    conversation.AddRange(compression.Messages);
                    contextCompressed?.Invoke(compression);
                }
            }

            if (++modelRequestCount > _options.MaximumModelRequests)
            {
                return new(
                    "model_request_limit",
                    iteration - 1,
                    modelRequestCount - 1,
                    toolCallCount);
            }

            modelRequestStarted?.Invoke(modelRequestCount);
            var response = await completeModelAsync(
                    iteration,
                    conversation,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            modelResponseReceived?.Invoke(response);
            if (stopRequested?.Invoke() == true)
            {
                return new(
                    "stopped",
                    iteration,
                    modelRequestCount,
                    toolCallCount);
            }

            var toolCalls = response.ToolCalls;
            if (toolCalls is not { Count: > 0 })
            {
                return new("completed", iteration, modelRequestCount, toolCallCount);
            }

            conversation.Add(new AgentChatMessage(
                "assistant",
                response.Text,
                ToolCalls: toolCalls));

            foreach (var toolCall in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++toolCallCount > _options.MaximumToolCalls)
                {
                    return new(
                        "tool_call_limit",
                        iteration,
                        modelRequestCount,
                        toolCallCount - 1);
                }

                var toolResult = await executeToolAsync(toolCall, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                conversation.Add(new AgentChatMessage(
                    "tool",
                    toolResult.Content,
                    ToolCallId: toolCall.Id));
                toolCallCompleted?.Invoke(toolCall, toolResult);
            }
        }

        return new(
            "max_iterations",
            _options.MaximumIterations,
            modelRequestCount,
            toolCallCount);
    }
}

public sealed record OpenCoworkRuntimeLoopOptions
{
    /// <summary>Zero follows OpenCoWork and means no fixed iteration limit.</summary>
    public int MaximumIterations { get; init; }

    public int MaximumModelRequests { get; init; } = 32;
    public int MaximumToolCalls { get; init; } = 64;
    public bool EnableContextCompression { get; init; } = true;
    public int ContextMessageLimit { get; init; } = OpenCoworkRuntimeContextCompactor.DefaultMessageLimit;
    public int ContextCharacterLimit { get; init; } = OpenCoworkRuntimeContextCompactor.DefaultCharacterLimit;
    public int ContextPreserveRecentMessages { get; init; } = OpenCoworkRuntimeContextCompactor.DefaultPreserveRecentMessages;
}

public sealed record OpenCoworkRuntimeToolResult(bool IsSuccess, string Content);

public sealed record OpenCoworkRuntimeLoopResult(
    string Reason,
    int Iterations,
    int ModelRequests,
    int ToolCalls)
{
    public bool IsCompleted => string.Equals(Reason, "completed", StringComparison.Ordinal);
}
