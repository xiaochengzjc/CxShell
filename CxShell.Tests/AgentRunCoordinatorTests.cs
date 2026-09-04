using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRunCoordinatorTests
{
    [Fact]
    public async Task RunPublishesOrderedLifecycleEventsAndRemovesActiveRun()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(_ => Task.FromResult(new AgentModelResponse(
                "server is healthy",
                provider.Model,
                provider.BuiltinId,
                3,
                4))));
        var events = new List<AgentRuntimeStreamEnvelope>();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            lock (events)
                events.Add(envelope);
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-ordered",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "check the server")]
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AgentRuntimeStreamEnvelope[] captured;
        lock (events)
            captured = events.ToArray();

        Assert.Equal(
            ["run_start", "run_phase", "run_phase", "run_checkpoint", "text_delta", "run_phase", "loop_end"],
            captured.SelectMany(envelope => envelope.Events).Select(@event => @event.Type));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L], captured.Select(envelope => envelope.Sequence));
        Assert.Equal("server is healthy", captured[4].Events[0].Text);
        Assert.Equal("completed", captured[6].Events[0].Reason);
        Assert.Empty(coordinator.GetActiveRuns());

        var summary = Assert.Single(coordinator.GetRecentRuns());
        Assert.Equal("completed", summary.Status);
        Assert.Equal("completed", summary.EndReason);
        Assert.NotNull(summary.CompletedAtUtc);
        Assert.Equal(7L, summary.EventCount);
        Assert.Equal(1, summary.ModelRequestCount);
        Assert.Equal(1, summary.ProviderRequestCount);
        Assert.Equal(0, summary.ProviderRetryCount);
        Assert.Equal(0, summary.ContextSummaryCount);
        Assert.Equal(summary, coordinator.GetRun(start.RunId));
    }

    [Fact]
    public async Task ProviderRetryCountsPhysicalRequestsWithoutIncreasingLogicalModelRequests()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        var attempts = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, _, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw AgentProviderException.Network(new IOException("temporary provider failure"));

                return Task.FromResult(new AgentModelResponse(
                    "recovered",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "provider-retry-counts"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var summary = Assert.Single(coordinator.GetRecentRuns());
        Assert.Equal(2, attempts);
        Assert.Equal(1, summary.ModelRequestCount);
        Assert.Equal(2, summary.ProviderRequestCount);
        Assert.Equal(1, summary.ProviderRetryCount);
        Assert.Equal("completed", summary.Status);
        Assert.Equal("completed", summary.EndReason);
        Assert.Equal(2, summary.Checkpoint?.ProviderRequestCount);
        Assert.Equal(1, summary.Checkpoint?.ProviderRetryCount);
    }

    [Fact]
    public async Task StreamingProviderPublishesTextDeltasWithoutDuplicatingTheFinalResponse()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StreamingStubModelClient(provider));
        var textEvents = new List<string>();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            foreach (var @event in envelope.Events)
            {
                if (@event.Type == "text_delta")
                    textEvents.Add(@event.Text ?? string.Empty);
                if (@event.Type == "loop_end")
                    completed.TrySetResult(true);
            }
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-streaming",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "check the server")]
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["hello from stream"], textEvents);
    }

    [Fact]
    public async Task StreamingProviderFlushesAnotherTextDeltaWhenBatchWindowExpires()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StreamingStubModelClient(
                provider,
                AgentRunCoordinator.StreamTextDeltaBatchInterval + TimeSpan.FromMilliseconds(75)));
        var textEvents = new List<string>();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            foreach (var @event in envelope.Events)
            {
                if (@event.Type == "text_delta")
                    textEvents.Add(@event.Text ?? string.Empty);
                if (@event.Type == "loop_end")
                    completed.TrySetResult(true);
            }
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-streaming-window",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "check the server")]
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["hello ", "from stream"], textEvents);
    }

    [Fact]
    public async Task CompletedRunEventsCanBeReadIncrementally()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(_ => Task.FromResult(new AgentModelResponse(
                "server is healthy",
                provider.Model,
                provider.BuiltinId))));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-events",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "check the server")]
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var first = coordinator.ReadEvents(start.RunId, limit: 1);
        Assert.NotNull(first);
        Assert.Equal(start.RunId, first.RunId);
        Assert.Equal(snapshot.SessionId.ToString("D"), first.SessionId);
        var firstEnvelope = Assert.Single(first.Events);
        Assert.Equal(1L, firstEnvelope.Sequence);
        Assert.True(first.HasMore);
        Assert.Equal(1L, first.NextSequence);
        Assert.Equal(1L, first.OldestSequence);
        Assert.Equal(7L, first.LatestSequence);

        var rest = coordinator.ReadEvents(start.RunId, first.NextSequence, limit: 100);
        Assert.NotNull(rest);
        Assert.Equal([2L, 3L, 4L, 5L, 6L, 7L], rest.Events.Select(envelope => envelope.Sequence));
        Assert.False(rest.HasMore);
        Assert.Equal(7L, rest.NextSequence);
        Assert.Null(coordinator.ReadEvents("missing-run"));
    }

    [Fact]
    public async Task CompletedRunHistoryIsBoundedAndEvictsTheOldestRun()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(_ => Task.FromResult(new AgentModelResponse(
                "ok",
                provider.Model,
                provider.BuiltinId))));

        for (var index = 0; index <= AgentRunCoordinator.MaximumRetainedRuns; index++)
        {
            var runId = $"bounded-run-{index}";
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = coordinator.Subscribe(envelope =>
            {
                if (envelope.RunId == runId &&
                    envelope.Events.Any(@event => @event.Type == "loop_end"))
                {
                    completed.TrySetResult(true);
                }
            });

            var start = coordinator.Start(new AgentRunRequest
            {
                RunId = runId,
                SessionId = snapshot.SessionId,
                Messages = [new AgentChatMessage("user", "check")]
            });

            Assert.True(start.Started);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Null(coordinator.GetRun("bounded-run-0"));
        Assert.NotNull(coordinator.GetRun($"bounded-run-{AgentRunCoordinator.MaximumRetainedRuns}"));
        Assert.Equal(
            AgentRunCoordinator.MaximumRetainedRuns,
            coordinator.GetRecentRuns(AgentRunCoordinator.MaximumRetainedRuns + 1).Count);
    }

    [Fact]
    public async Task CancelPublishesAbortedLoopEnd()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(async cancellationToken =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentModelResponse("never", provider.Model, provider.BuiltinId);
            }));
        var completed = new TaskCompletionSource<AgentRuntimeStreamEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(envelope);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-cancel",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "wait")]
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancel = coordinator.Cancel(start.RunId);
        var terminal = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancel.Cancelled);
        Assert.Equal("aborted", terminal.Events.Single().Reason);
        Assert.Empty(coordinator.GetActiveRuns());
    }

    [Fact]
    public async Task FollowUpMessagesAreAppliedBeforeTheNextModelTurn()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var toolStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource<AgentModelRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    async (_, cancellationToken) =>
                    {
                        toolStarted.TrySetResult(true);
                        await releaseTool.Task.WaitAsync(cancellationToken);
                        return "command completed";
                    })
            ]));
        var provider = CreateProvider();
        var requestCount = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "call-follow-up",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"printf ready\"}")
                        ]));
                }

                secondRequest.TrySetResult(request);
                return Task.FromResult(new AgentModelResponse(
                    "follow-up completed",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-follow-up",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "inspect the server")]
        });
        Assert.True(start.Started);
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var append = coordinator.AppendMessages(
            start.RunId,
            [new AgentChatMessage("user", "also verify the Java runtime")]);
        Assert.True(append.Appended);
        Assert.Equal(1, append.MessageCount);

        releaseTool.TrySetResult(true);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var modelRequest = await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(
            modelRequest.Messages,
            message => message.Role == "user" && message.Content == "also verify the Java runtime");
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task GracefulStopWaitsForTheCurrentToolAndPublishesStopped()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var toolStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    async (_, cancellationToken) =>
                    {
                        toolStarted.TrySetResult(true);
                        await releaseTool.Task.WaitAsync(cancellationToken);
                        return "command completed";
                    })
            ]));
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(_ => Task.FromResult(new AgentModelResponse(
                string.Empty,
                provider.Model,
                provider.BuiltinId,
                ToolCalls:
                [
                    new AgentToolCall(
                        "call-stop",
                        AgentRunCoordinator.SessionCommandToolName,
                        "{\"command\":\"printf ready\"}")
                ]))));
        var terminalEvent = new TaskCompletionSource<AgentRuntimeStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            var loopEnd = envelope.Events.FirstOrDefault(@event => @event.Type == "loop_end");
            if (loopEnd != null)
                terminalEvent.TrySetResult(loopEnd);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-stop",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "run a check")]
        });
        Assert.True(start.Started);
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = coordinator.RequestStop(start.RunId);
        Assert.True(stop.Requested);
        releaseTool.TrySetResult(true);
        var end = await terminalEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("stopped", end.Reason);
        Assert.Equal("stopped", coordinator.GetRun(start.RunId)?.Status);
        Assert.Empty(coordinator.GetActiveRuns());
    }

    [Fact]
    public async Task TimeoutPublishesErrorBeforeTimeoutLoopEnd()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentModelResponse("never", provider.Model, provider.BuiltinId);
            }));
        var events = new List<AgentRuntimeStreamEvent>();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            lock (events)
                events.AddRange(envelope.Events);
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "run-timeout",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "wait")],
            Timeout = TimeSpan.FromMilliseconds(100)
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AgentRuntimeStreamEvent[] captured;
        lock (events)
            captured = events.ToArray();

        Assert.Equal(
            ["run_start", "run_phase", "run_phase", "run_checkpoint", "error", "run_phase", "loop_end"],
            captured.Select(@event => @event.Type));
        Assert.Equal("Timeout", captured[4].ErrorType);
        Assert.Equal("timeout", captured[6].Reason);
    }

    [Fact]
    public async Task ToolCallRunsThroughGatewayAndItsOutputIsReturnedToTheModel()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var executedCommand = string.Empty;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        executedCommand = request.Command;
                        return Task.FromResult("Linux agent-host 6.8");
                    })
            ]));
        var provider = CreateProvider();
        var callCount = 0;
        string? toolResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "tool-1",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"uname -a\"}")
                        ]));
                }

                toolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "The host is running Linux.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "tool-run"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("uname -a", executedCommand);
        Assert.Equal(2, callCount);
        Assert.Contains("Linux agent-host 6.8", toolResult, StringComparison.Ordinal);
        Assert.Contains("Sent", toolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatModeRejectsUnexpectedProviderToolCalls()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = CreateProvider();
        var modelCalls = 0;
        string? toolResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "unexpected-chat-tool",
                                AgentRunCoordinator.SessionInfoToolName,
                                "{}")
                        ]));
                }

                toolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "Chat mode answer.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "chat-tool-guard",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "just chat")],
            Mode = AgentChatMode.Chat
        });

        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, modelCalls);
        Assert.Contains("disabled in Chat mode", toolResult, StringComparison.Ordinal);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task LifecycleEventsExposeProgressCheckpoints()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult("ready"))
            ]));
        var provider = CreateProvider();
        var modelCalls = 0;
        var events = new List<AgentRuntimeStreamEvent>();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, _, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "checkpoint-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"printf ready\"}")
                        ]));
                }

                return Task.FromResult(new AgentModelResponse(
                    "The command completed.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            lock (events)
                events.AddRange(envelope.Events);
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "checkpoint-run"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AgentRuntimeStreamEvent[] captured;
        lock (events)
            captured = events.ToArray();

        var runStart = Assert.Single(captured, @event => @event.Type == "run_start");
        var toolStart = Assert.Single(captured, @event => @event.Type == "tool_call_update");
        var toolResult = Assert.Single(captured, @event => @event.Type == "tool_call_result");
        var loopEnd = Assert.Single(captured, @event => @event.Type == "loop_end");
        var modelRequest = Assert.Single(captured, @event =>
            @event.Type == "run_checkpoint" &&
            @event.Checkpoint?.Phase == "model_request" &&
            @event.Checkpoint.ModelRequestCount == 2);
        Assert.Equal("running", runStart.Checkpoint?.Status);
        Assert.Equal("tool_call", toolStart.Checkpoint?.Phase);
        Assert.Equal("running", toolStart.Checkpoint?.Status);
        Assert.Equal("executing", toolStart.Checkpoint?.ToolExecutionState);
        Assert.False(toolStart.Checkpoint?.ToolOutcomeCertain);
        Assert.Equal("completed", toolResult.Checkpoint?.Status);
        Assert.Equal("completed", toolResult.Checkpoint?.ToolExecutionState);
        Assert.True(toolResult.Checkpoint?.ToolOutcomeCertain);
        Assert.True(toolResult.Checkpoint?.ToolRemoteCompletionConfirmed);
        Assert.False(toolResult.Checkpoint?.ToolRetrySafe);
        Assert.Equal("completed", loopEnd.Checkpoint?.Status);
        Assert.Equal(2, loopEnd.Checkpoint?.ModelRequestCount);
        Assert.Equal(1, loopEnd.Checkpoint?.ToolCallCount);
        Assert.Equal(AgentRunCoordinator.SessionCommandToolName, modelRequest.Checkpoint?.ToolName);
        Assert.Equal(loopEnd.Checkpoint, coordinator.GetRun(start.RunId)?.Checkpoint);
    }

    [Fact]
    public async Task CommandProgressPublishesSeparateStreamsAndTruncatesLiveOutput()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var provider = CreateProvider();
        var events = new List<AgentRuntimeStreamEvent>();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask,
            runCommand: null,
            runCommandResult: null,
            runCommandProgressResult: (_, _, progressReceived) =>
            {
                progressReceived?.Invoke(new AgentCommandProgress(
                    Guid.NewGuid(),
                    snapshot.SessionId,
                    "standard output",
                    ElapsedMs: 10));
                progressReceived?.Invoke(new AgentCommandProgress(
                    Guid.NewGuid(),
                    snapshot.SessionId,
                    "standard error",
                    IsError: true,
                    ElapsedMs: 11));
                progressReceived?.Invoke(new AgentCommandProgress(
                    Guid.NewGuid(),
                    snapshot.SessionId,
                    new string('x', AgentRunCoordinator.MaximumCommandOutputCharactersPerTool + 1),
                    ElapsedMs: 12));
                return Task.FromResult(new AgentCommandExecutionResult(true, "completed"));
            });
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => [endpoint]));
        var modelCalls = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, _, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "progress-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"printf output\"}")
                        ]));
                }

                return Task.FromResult(new AgentModelResponse(
                    "The command completed.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        using var subscription = coordinator.Subscribe(envelope =>
        {
            lock (events)
                events.AddRange(envelope.Events);
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "command-progress"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AgentRuntimeStreamEvent[] captured;
        lock (events)
            captured = events.ToArray();

        var output = captured.Where(@event => @event.Type == "command_output_delta").ToArray();
        Assert.Contains(output, @event => @event.Stream == "stdout" && @event.Text == "standard output");
        Assert.Contains(output, @event => @event.Stream == "stderr" && @event.Text == "standard error");
        Assert.Contains(captured, @event => @event.Type == "command_output_truncated");
        Assert.All(
            coordinator.GetRun(start.RunId)!.Steps,
            step => Assert.True(AgentRunStepStatuses.IsTerminal(step.Status)));
        Assert.Contains(
            coordinator.GetRun(start.RunId)!.Steps,
            step => step.Id == "summary" && step.Status == AgentRunStepStatuses.Completed);
    }

    [Fact]
    public async Task PasswordRequiredCommandPausesUntilCredentialIsProvidedWithoutPublishingIt()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var credentialRequest = new TaskCompletionSource<AgentRuntimeStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentCommandRequest? retriedRequest = null;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    runCommand: null,
                    runCommandResult: (request, _) =>
                    {
                        if (retriedRequest == null)
                        {
                            retriedRequest = request;
                            return Task.FromResult(new AgentCommandExecutionResult(
                                RemoteCompletionConfirmed: true,
                                Error: "sudo: a password is required",
                                ExitCode: 1));
                        }

                        retriedRequest = request;
                        return Task.FromResult(new AgentCommandExecutionResult(
                            RemoteCompletionConfirmed: true,
                            Output: "installed successfully",
                            ExitCode: 0));
                    })
            ]));
        var provider = CreateProvider();
        var modelCalls = 0;
        string? modelToolResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "credential-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"sudo -n apt-get install java\"}")
                        ]));
                }

                modelToolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "Java was installed.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        using var subscription = coordinator.Subscribe(envelope =>
        {
            foreach (var @event in envelope.Events)
            {
                if (@event.Type == "credential_required")
                    credentialRequest.TrySetResult(@event);
                if (@event.Type == "loop_end")
                    completed.TrySetResult(true);
            }
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "credential-run",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "install Java")]
        });
        Assert.True(start.Started);

        var requestEvent = await credentialRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("password", requestEvent.CredentialKind);
        Assert.NotNull(requestEvent.CredentialRequestId);
        Assert.DoesNotContain("secret", requestEvent.CredentialPrompt ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(modelToolResult);

        var provided = coordinator.ProvideCredential(
            start.RunId,
            requestEvent.CredentialRequestId!,
            "secret",
            rememberForRun: true);
        Assert.True(provided.Provided);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(retriedRequest);
        Assert.Equal("secret", retriedRequest!.SensitiveInput);
        Assert.Contains("sudo -S -p '' sh -c", retriedRequest.Command, StringComparison.Ordinal);
        Assert.Contains("apt-get install java", retriedRequest.Command, StringComparison.Ordinal);
        Assert.Contains("sudo -S -p ''", retriedRequest.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", modelToolResult ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task ApprovedCommandPausesForCredentialAfterRemoteSudoFailure()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var approvalNeeded = new TaskCompletionSource<(string RunId, string ToolCallId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var credentialNeeded = new TaskCompletionSource<AgentRuntimeStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        AgentCommandRequest? retriedRequest = null;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        if (Interlocked.Increment(ref executionCount) == 1)
                        {
                            return Task.FromException<string>(
                                new InvalidOperationException("sudo: a password is required"));
                        }

                        retriedRequest = request;
                        return Task.FromResult("nginx and php8 are ready");
                    })
            ]),
            new AgentPermissionPolicy
            {
                PermissionMode = AgentPermissionPolicy.RiskBasedApprovalMode
            });
        var provider = CreateProvider();
        var modelCalls = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "approved-credential-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"sudo apt-get update && sudo DEBIAN_FRONTEND=noninteractive apt-get install nginx php8.2\"}")
                        ]));
                }

                return Task.FromResult(new AgentModelResponse(
                    "The web environment is ready.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        using var subscription = coordinator.Subscribe(envelope =>
        {
            foreach (var @event in envelope.Events)
            {
                if (@event.Type == "tool_call_approval_required")
                {
                    approvalNeeded.TrySetResult((envelope.RunId, @event.ToolCallId!));
                }

                if (@event.Type == "credential_required")
                    credentialNeeded.TrySetResult(@event);
                if (@event.Type == "loop_end")
                    completed.TrySetResult(true);
            }
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "approved-credential-run",
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "install the web environment")]
        });
        Assert.True(start.Started);

        var approval = await approvalNeeded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.Approve(approval.RunId, approval.ToolCallId).Approved);

        var credential = await credentialNeeded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("password", credential.CredentialKind);
        Assert.Null(coordinator.GetRun(start.RunId)?.CompletedAtUtc);
        Assert.True(coordinator.ProvideCredential(
            start.RunId,
            credential.CredentialRequestId!,
            "secret",
            rememberForRun: false).Provided);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, executionCount);
        Assert.NotNull(retriedRequest);
        Assert.Contains("sudo -S -p '' sh -c", retriedRequest!.Command, StringComparison.Ordinal);
        Assert.Contains("apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install nginx php8.2", retriedRequest.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo -n", retriedRequest.Command, StringComparison.Ordinal);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task RejectedCredentialRequestsAnotherValueWithoutEndingTheRun()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var firstCredential = new TaskCompletionSource<AgentRuntimeStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCredential = new TaskCompletionSource<AgentRuntimeStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var credentialEventCount = 0;
        AgentCommandRequest? rejectedRequest = null;
        AgentCommandRequest? successfulRequest = null;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        switch (Interlocked.Increment(ref executionCount))
                        {
                            case 1:
                                return Task.FromException<string>(
                                    new InvalidOperationException("sudo: a password is required"));
                            case 2:
                                rejectedRequest = request;
                                return Task.FromException<string>(
                                    new InvalidOperationException("sudo: sorry, try again."));
                            default:
                                successfulRequest = request;
                                return Task.FromResult(
                                    "command completed with wrong-password and correct-password");
                        }
                    })
            ]));
        var provider = CreateProvider();
        var modelCalls = 0;
        string? modelToolResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "retry-credential-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"sudo -n apt-get install nginx\"}")
                        ]));
                }

                modelToolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "Nginx was installed.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        using var subscription = coordinator.Subscribe(envelope =>
        {
            foreach (var @event in envelope.Events.Where(@event => @event.Type == "credential_required"))
            {
                if (Interlocked.Increment(ref credentialEventCount) == 1)
                    firstCredential.TrySetResult(@event);
                else
                    secondCredential.TrySetResult(@event);
            }

            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "credential-retry-run"));
        Assert.True(start.Started);

        var firstRequest = await firstCredential.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, firstRequest.Attempt);
        Assert.Equal(AgentRunCoordinator.MaximumCredentialAttempts, firstRequest.MaxAttempts);
        Assert.True(coordinator.ProvideCredential(
            start.RunId,
            firstRequest.CredentialRequestId!,
            "wrong-password",
            rememberForRun: true).Provided);

        var secondRequest = await secondCredential.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, secondRequest.Attempt);
        Assert.DoesNotContain("wrong-password", secondRequest.CredentialPrompt ?? string.Empty, StringComparison.Ordinal);
        Assert.True(coordinator.ProvideCredential(
            start.RunId,
            secondRequest.CredentialRequestId!,
            "correct-password",
            rememberForRun: false).Provided);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, executionCount);
        Assert.Equal("wrong-password", rejectedRequest?.SensitiveInput);
        Assert.Equal("correct-password", successfulRequest?.SensitiveInput);
        Assert.DoesNotContain("wrong-password", modelToolResult ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-password", modelToolResult ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task RememberedCredentialIsReusedByTheNextToolCallInTheSameRun()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var credentialRequest = new TaskCompletionSource<AgentRuntimeStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var requests = new List<AgentCommandRequest>();
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        lock (requests)
                            requests.Add(request);

                        Interlocked.Increment(ref executionCount);
                        return request.SensitiveInput == null
                            ? Task.FromException<string>(new InvalidOperationException("sudo: a password is required"))
                            : Task.FromResult("command completed");
                    })
            ]));
        var provider = CreateProvider();
        var modelCalls = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, _, _) =>
            {
                if (Interlocked.Increment(ref modelCalls) == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "first-sudo-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"sudo -n apt-get update\"}"),
                            new AgentToolCall(
                                "second-sudo-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"sudo -n apt-get install nginx\"}")
                        ]));
                }

                return Task.FromResult(new AgentModelResponse(
                    "Both commands completed.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        using var subscription = coordinator.Subscribe(envelope =>
        {
            var request = envelope.Events.FirstOrDefault(@event => @event.Type == "credential_required");
            if (request != null)
                credentialRequest.TrySetResult(request);
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "credential-cache-run"));
        Assert.True(start.Started);
        var credential = await credentialRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.ProvideCredential(
            start.RunId,
            credential.CredentialRequestId!,
            "run-password",
            rememberForRun: true).Provided);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AgentCommandRequest[] captured;
        lock (requests)
            captured = requests.ToArray();
        Assert.Equal(4, captured.Length);
        Assert.Null(captured[0].SensitiveInput);
        Assert.Equal("run-password", captured[1].SensitiveInput);
        Assert.Null(captured[2].SensitiveInput);
        Assert.Equal("run-password", captured[3].SensitiveInput);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task RunCanContinuePastLegacyIterationLimitUntilModelCompletes()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult("step completed"))
            ]));
        var provider = CreateProvider();
        var callCount = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, _, _) =>
            {
                callCount++;
                if (callCount <= 8)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                $"long-run-tool-{callCount}",
                                AgentRunCoordinator.SessionCommandToolName,
                                $"{{\"command\":\"printf step-{callCount}\"}}")
                        ]));
                }

                return Task.FromResult(new AgentModelResponse(
                    "The long-running task is complete.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "past-legacy-limit"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(9, callCount);
        var summary = Assert.Single(coordinator.GetRecentRuns());
        Assert.Equal("completed", summary.Status);
        Assert.Equal("completed", summary.EndReason);
    }

    [Fact]
    public async Task AgentCanOpenSavedSessionBeforeExecutingCommandWhenRunStartsWithoutSession()
    {
        var savedSession = new AgentSavedSessionSnapshot
        {
            SavedSessionId = Guid.NewGuid(),
            Name = "Saved SSH",
            Path = "Operations/Saved SSH",
            Protocol = SessionProtocol.SSH,
            Host = "saved.example",
            Port = 22,
            Username = "operator"
        };
        var openedSnapshot = CreateSnapshot(isConnected: true);
        var endpoints = new List<IAgentSessionEndpoint>();
        var executedCommand = string.Empty;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(
                () => endpoints,
                _ => Task.FromResult<IReadOnlyList<AgentSavedSessionSnapshot>>([savedSession]),
                (_, _) =>
                {
                    endpoints.Add(new AgentSessionEndpoint(
                        () => openedSnapshot,
                        (_, _) => Task.CompletedTask,
                        (request, _) =>
                        {
                            executedCommand = request.Command;
                            return Task.FromResult("command output");
                        }));
                    return Task.FromResult(new AgentSessionOpenResult(
                        AgentSessionOpenStatus.Opened,
                        openedSnapshot,
                        AgentOwned: true));
                },
                (_, _) => Task.FromResult(new AgentSessionCloseResult(AgentSessionCloseStatus.Closed))));
        var provider = CreateProvider();
        var modelCalls = 0;
        var approvalNeeded = new TaskCompletionSource<(string RunId, string ToolCallId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, _, _) =>
            {
                switch (Interlocked.Increment(ref modelCalls))
                {
                    case 1:
                        return Task.FromResult(new AgentModelResponse(
                            string.Empty,
                            provider.Model,
                            provider.BuiltinId,
                            ToolCalls:
                            [
                                new AgentToolCall(
                                    "open-saved-session",
                                    AgentRunCoordinator.OpenSessionToolName,
                                    $"{{\"savedSessionId\":\"{savedSession.SavedSessionId:D}\",\"reason\":\"inspect the saved host\"}}")
                            ]));
                    case 2:
                        return Task.FromResult(new AgentModelResponse(
                            string.Empty,
                            provider.Model,
                            provider.BuiltinId,
                            ToolCalls:
                            [
                                new AgentToolCall(
                                    "run-command-after-open",
                                    AgentRunCoordinator.SessionCommandToolName,
                                    "{\"command\":\"printf ready\"}")
                            ]));
                    default:
                        return Task.FromResult(new AgentModelResponse(
                            "The saved SSH session is ready.",
                            provider.Model,
                            provider.BuiltinId));
                }
            }));
        using var subscription = coordinator.Subscribe(envelope =>
        {
            var approval = envelope.Events.FirstOrDefault(@event =>
                @event.Type == "tool_call_approval_required");
            if (approval != null)
                approvalNeeded.TrySetResult((envelope.RunId, approval.ToolCallId!));
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(new AgentRunRequest
        {
            RunId = "sessionless-open",
            SessionId = Guid.Empty,
            Messages = [new AgentChatMessage("user", "open the saved SSH session and run a check")],
            Mode = AgentChatMode.Agent
        });

        Assert.True(start.Started);
        var pendingApproval = await approvalNeeded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.Approve(pendingApproval.RunId, pendingApproval.ToolCallId).Approved);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("printf ready", executedCommand);
        Assert.Equal(3, modelCalls);
        Assert.Equal(openedSnapshot.SessionId.ToString("D"), coordinator.GetRun(start.RunId)?.SessionId);
        Assert.Equal("completed", coordinator.GetRun(start.RunId)?.Status);
    }

    [Fact]
    public async Task RepeatedFailedCommandReturnsGuidanceToChooseAnotherApproach()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (_, _) => Task.FromException<string>(new InvalidOperationException("download source unavailable")))
            ]));
        var provider = CreateProvider();
        var callCount = 0;
        string? repeatedFailureResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                $"failed-tool-{callCount}",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"download-java-runtime\"}")
                        ]));
                }

                repeatedFailureResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "I will use another download source.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "repeated-failure"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, callCount);
        Assert.Contains("download source unavailable", repeatedFailureResult, StringComparison.Ordinal);
        Assert.Contains("agentGuidance", repeatedFailureResult, StringComparison.Ordinal);
        Assert.Contains("Do not retry it unchanged", repeatedFailureResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticToolUsesFixedPlanAndReturnsCollectedOutput()
    {
        var snapshot = CreateSnapshot(isConnected: true) with { Platform = "Linux/Unix" };
        string? executedCommand = null;
        string? toolResult = null;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        executedCommand = request.Command;
                        return Task.FromResult("=== disk ===\n/dev/sda1 100G 42G 58% /");
                    })
            ]));
        var provider = CreateProvider();
        var calls = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "diagnostic-1",
                                AgentRunCoordinator.DiagnosticRunToolName,
                                "{\"scope\":\"disk\"}")
                        ]));
                }

                toolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "The root disk is 58% used.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "diagnostic-run"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("df -P -h", executedCommand, StringComparison.Ordinal);
        Assert.Contains("diagnostic disk", toolResult, StringComparison.Ordinal);
        Assert.Contains("58%", toolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RdpRunbookIsRestrictedToWindowsAndReturnsCollectedOutput()
    {
        var snapshot = CreateSnapshot(isConnected: true) with { Platform = "Windows" };
        string? executedCommand = null;
        string? toolResult = null;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        executedCommand = request.Command;
                        return Task.FromResult("=== rdp ===\nStatus=Running; fDenyTSConnections=0");
                    })
            ]));
        var provider = CreateProvider();
        var calls = 0;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "runbook-1",
                                AgentRunCoordinator.RunbookRunToolName,
                                "{\"scope\":\"rdp\"}")
                        ]));
                }

                toolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "Remote Desktop service is running.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "rdp-runbook"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("powershell.exe", executedCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runbook rdp", toolResult, StringComparison.Ordinal);
        Assert.Contains("Running", toolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetDiagnosticToolReturnsPerSessionSummaryAndOutput()
    {
        var linux = CreateSnapshot(isConnected: true) with
        {
            Name = "Linux fleet host",
            Host = "linux.fleet.test",
            Platform = "Linux/Unix"
        };
        var windows = CreateSnapshot(isConnected: true) with
        {
            Name = "Windows fleet host",
            Host = "windows.fleet.test",
            Platform = "Windows"
        };
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => linux,
                    (_, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult("linux fleet output")),
                new AgentSessionEndpoint(
                    () => windows,
                    (_, _) => Task.CompletedTask,
                    (_, _) => Task.FromResult("windows fleet output"))
            ]));
        var provider = CreateProvider();
        var calls = 0;
        string? toolResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "fleet-1",
                                AgentRunCoordinator.FleetDiagnosticToolName,
                                "{\"scope\":\"disk\"}")
                        ]));
                }

                toolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "Both fleet hosts returned disk information.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(linux, "fleet-run"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, calls);
        Assert.Contains("targetCount", toolResult, StringComparison.Ordinal);
        Assert.Contains("successCount", toolResult, StringComparison.Ordinal);
        Assert.Contains("linux.fleet.test", toolResult, StringComparison.Ordinal);
        Assert.Contains("windows.fleet.test", toolResult, StringComparison.Ordinal);
        Assert.Contains("linux fleet output", toolResult, StringComparison.Ordinal);
        Assert.Contains("windows fleet output", toolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DangerousToolCallWaitsForApprovalBeforeRetrying()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var executedCommand = string.Empty;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (request, _) =>
                    {
                        executedCommand = request.Command;
                        return Task.FromResult("reboot requested");
                    })
            ]));
        var provider = CreateProvider();
        var calls = 0;
        var approvalNeeded = new TaskCompletionSource<(string RunId, string ToolCallId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "dangerous-tool-1",
                                AgentRunCoordinator.SessionCommandToolName,
                                "{\"command\":\"sudo reboot\"}")
                        ]));
                }

                return Task.FromResult(new AgentModelResponse(
                    "The reboot command was sent.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            var approval = envelope.Events.FirstOrDefault(@event =>
                @event.Type == "tool_call_approval_required");
            if (approval != null)
                approvalNeeded.TrySetResult((envelope.RunId, approval.ToolCallId!));
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "approval-run"));
        Assert.True(start.Started);
        var pending = await approvalNeeded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var approval = coordinator.Approve(pending.RunId, pending.ToolCallId);

        Assert.True(approval.Decided);
        Assert.True(approval.Approved);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
        Assert.Equal("sudo reboot", executedCommand);
    }

    [Fact]
    public async Task OversizedToolCallIsRejectedBeforeGatewayDispatch()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var gatewayDispatchCount = 0;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) => Task.CompletedTask,
                    (_, _) =>
                    {
                        gatewayDispatchCount++;
                        return Task.FromResult("must not run");
                    })
            ]));
        var provider = CreateProvider();
        var calls = 0;
        string? toolResult = null;
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient((_, request, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new AgentModelResponse(
                        string.Empty,
                        provider.Model,
                        provider.BuiltinId,
                        ToolCalls:
                        [
                            new AgentToolCall(
                                "oversized-tool",
                                AgentRunCoordinator.SessionCommandToolName,
                                $"{{\"command\":\"{new string('x', AgentRunCoordinator.MaximumToolArgumentsCharacters + 1)}\"}}")
                        ]));
                }

                toolResult = request.Messages.Last().Content;
                return Task.FromResult(new AgentModelResponse(
                    "The oversized tool call was rejected.",
                    provider.Model,
                    provider.BuiltinId));
            }));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });

        var start = coordinator.Start(CreateRequest(snapshot, "oversized-tool-run"));
        Assert.True(start.Started);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, gatewayDispatchCount);
        Assert.Contains("Tool arguments cannot exceed", toolResult, StringComparison.Ordinal);
    }

    [Fact]
    public void RunRejectsDisconnectedSessionAndDuplicateRunId()
    {
        var disconnected = CreateSnapshot(isConnected: false);
        using var disconnectedGateway = CreateGateway(disconnected);
        using var disconnectedCoordinator = new AgentRunCoordinator(
            disconnectedGateway,
            CreateProvider,
            new StubAgentModelClient(_ => Task.FromResult(new AgentModelResponse("ok", "model", "test"))));

        var disconnectedResult = disconnectedCoordinator.Start(CreateRequest(disconnected, "disconnected"));

        Assert.False(disconnectedResult.Started);
        Assert.Contains("not connected", disconnectedResult.Error, StringComparison.OrdinalIgnoreCase);

        var connected = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(connected);
        var provider = CreateProvider();
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new StubAgentModelClient(async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentModelResponse("ok", "model", "test");
            }));
        var first = coordinator.Start(CreateRequest(connected, "duplicate"));
        var second = coordinator.Start(CreateRequest(connected, "duplicate"));

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Contains("already exists", second.Error, StringComparison.OrdinalIgnoreCase);
        coordinator.Cancel(first.RunId);
    }

    [Fact]
    public async Task ResumeCreatesANewRunAndConsumesTheInterruptedRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            var snapshot = CreateSnapshot(isConnected: true);
            var store = new JsonAgentRunHistoryStore(path);
            var previousRunId = "interrupted-for-resume";
            store.SaveRecoverable(
            [
                new AgentRunRecoveryState(
                    new AgentRuntimeRunSnapshot(
                        previousRunId,
                        snapshot.SessionId.ToString("D"),
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                        "interrupted",
                        Model: "test-model",
                        EndReason: "application_restart",
                        CanResume: true,
                        Checkpoint: new AgentRunCheckpoint(
                            3,
                            "credential",
                            "waiting_for_input",
                            ToolCallId: "tool-3",
                            ToolName: "session_command",
                            ModelRequestCount: 2,
                            ToolCallCount: 1,
                            Detail: "Waiting for input.",
                            ToolExecutionState: "dispatched",
                            ToolOutcomeCertain: false,
                            ToolRemoteCompletionConfirmed: false,
                            ToolRetrySafe: false)),
                    [new AgentChatMessage("user", "check the host")])
            ]);

            using var gateway = CreateGateway(snapshot);
            var provider = CreateProvider();
            AgentModelRequest? resumedRequest = null;
            using var coordinator = new AgentRunCoordinator(
                gateway,
                () => provider,
                new StubAgentModelClient((_, request, _) =>
                {
                    resumedRequest = request;
                    return Task.FromResult(new AgentModelResponse(
                        "the host is healthy",
                        provider.Model,
                        provider.BuiltinId));
                }),
                store);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = coordinator.Subscribe(envelope =>
            {
                if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                    completed.TrySetResult(true);
            });

            var resumed = coordinator.Resume(previousRunId);

            Assert.True(resumed.Resumed);
            Assert.NotEqual(previousRunId, resumed.RunId);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(coordinator.GetRun(previousRunId)!.CanResume);
            Assert.Equal("completed", coordinator.GetRun(resumed.RunId)!.Status);
            Assert.Contains(
                resumedRequest!.Messages,
                message => message.Role == "system" &&
                           message.Content.Contains("waiting_for_input", StringComparison.Ordinal) &&
                           message.Content.Contains("dispatched", StringComparison.Ordinal) &&
                           message.Content.Contains("Remote completion confirmed: no", StringComparison.Ordinal) &&
                           message.Content.Contains("Do not blindly repeat", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            var recoveryPath = path + ".recovery";
            if (File.Exists(recoveryPath))
                File.Delete(recoveryPath);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisposingAnActiveRunPersistsAnInterruptedCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            var snapshot = CreateSnapshot(isConnected: true);
            var store = new JsonAgentRunHistoryStore(path);
            var provider = CreateProvider();
            var modelStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gateway = CreateGateway(snapshot);
            using var coordinator = new AgentRunCoordinator(
                gateway,
                () => provider,
                new StubAgentModelClient(async cancellationToken =>
                {
                    modelStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new AgentModelResponse("never", provider.Model, provider.BuiltinId);
                }),
                store);

            var start = coordinator.Start(CreateRequest(snapshot, "interrupted-checkpoint"));
            Assert.True(start.Started);
            await modelStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            coordinator.Dispose();

            var recovery = Assert.Single(store.LoadRecoverable());
            Assert.Equal(start.RunId, recovery.Snapshot.RunId);
            Assert.Equal("interrupted", recovery.Snapshot.Status);
            Assert.True(recovery.Snapshot.CanResume);
            Assert.Equal("interrupted", recovery.Checkpoint?.Status);
            Assert.Equal(1, recovery.Checkpoint?.ModelRequestCount);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            var recoveryPath = path + ".recovery";
            if (File.Exists(recoveryPath))
                File.Delete(recoveryPath);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static AgentRunRequest CreateRequest(AgentSessionSnapshot snapshot, string runId)
        => new()
        {
            RunId = runId,
            SessionId = snapshot.SessionId,
            Messages = [new AgentChatMessage("user", "hello")]
        };

    private static AgentProviderSettings CreateProvider()
        => new()
        {
            Enabled = true,
            BuiltinId = "test-provider",
            Name = "Test provider",
            BaseUrl = "https://provider.example",
            Model = "test-model",
            RequiresApiKey = false
        };

    private static AgentSessionGateway CreateGateway(AgentSessionSnapshot snapshot)
    {
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask);
        return new AgentSessionGateway(new DelegateAgentSessionHost(() => [endpoint]));
    }

    private static AgentSessionSnapshot CreateSnapshot(bool isConnected)
    {
        var session = new SessionInfo
        {
            Name = "Agent run test",
            Host = "agent.example",
            Username = "operator",
            Protocol = SessionProtocol.SSH
        };
        return AgentSessionSnapshot.FromSession(session, isConnected);
    }

    private sealed class StubAgentModelClient : IAgentModelClient
    {
        private readonly Func<AgentProviderSettings, AgentModelRequest, CancellationToken, Task<AgentModelResponse>> _handler;

        public StubAgentModelClient(Func<CancellationToken, Task<AgentModelResponse>> handler)
        {
            _handler = (_, _, cancellationToken) => handler(cancellationToken);
        }

        public StubAgentModelClient(
            Func<AgentProviderSettings, AgentModelRequest, CancellationToken, Task<AgentModelResponse>> handler)
        {
            _handler = handler;
        }

        public Task<AgentModelResponse> CompleteAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
            => _handler(provider, request, cancellationToken);
    }

    private sealed class StreamingStubModelClient : IAgentModelClient, IAgentStreamingModelClient
    {
        private readonly AgentProviderSettings _provider;
        private readonly TimeSpan? _delayBetweenChunks;

        public StreamingStubModelClient(
            AgentProviderSettings provider,
            TimeSpan? delayBetweenChunks = null)
        {
            _provider = provider;
            _delayBetweenChunks = delayBetweenChunks;
        }

        public Task<AgentModelResponse> CompleteAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The streaming path was not selected.");

        public async Task<AgentModelResponse> CompleteStreamingAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            Action<AgentModelStreamChunk> onChunk,
            CancellationToken cancellationToken = default)
        {
            onChunk(new AgentModelStreamChunk("hello "));
            if (_delayBetweenChunks is { } delay)
                await Task.Delay(delay, cancellationToken);
            onChunk(new AgentModelStreamChunk("from stream"));
            return new AgentModelResponse(
                "hello from stream",
                _provider.Model,
                _provider.BuiltinId);
        }
    }
}
