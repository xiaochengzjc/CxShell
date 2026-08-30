using System.Text.Json;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeHostTests
{
    [Fact]
    public async Task HostRoutesRequestsAndPublishesModuleEvents()
    {
        var module = new TestModule("echo", "agent/echo");
        using var host = new AgentRuntimeHost([module]);
        var events = new List<AgentRuntimeModuleEvent>();
        using var subscription = host.Subscribe(events.Add);

        using var document = JsonDocument.Parse("{\"value\":42}");
        var response = await host.DispatchAsync(
            " request-1 ",
            " agent/echo ",
            document.RootElement);

        Assert.True(response.Ok);
        Assert.Equal("request-1", response.RequestId);
        Assert.Equal("agent/echo", module.LastContext!.Request.Method);
        Assert.Equal("request-1", module.LastContext.Request.RequestId);
        Assert.Equal("echo", module.LastContext.ModuleName);
        Assert.Single(events);
        Assert.Equal("started", events[0].EventName);
        Assert.Equal("request-1", events[0].RequestId);
        Assert.Equal("agent/echo", events[0].Method);
    }

    [Fact]
    public void HostExposesStableModuleAndMethodDiscovery()
    {
        using var host = new AgentRuntimeHost(
        [
            new TestModule("second", "z/method", "a/method"),
            new TestModule("first", "first/method")
        ]);

        Assert.Equal(
            ["a/method", "first/method", "runtime/cancel", "z/method"],
            host.Methods);
        Assert.Equal(
            ["second", "first"],
            host.Modules.Select(module => module.Name));
        Assert.Equal(
            ["a/method", "z/method"],
            host.Modules[0].Methods);
    }

    [Fact]
    public async Task HostRejectsDuplicateModulesAndMethods()
    {
        using var host = new AgentRuntimeHost([new TestModule("first", "agent/one")]);

        Assert.Throws<InvalidOperationException>(
            () => host.RegisterModule(new TestModule("first", "agent/two")));
        Assert.Throws<InvalidOperationException>(
            () => host.RegisterModule(new TestModule("second", "agent/one")));
        Assert.Throws<InvalidOperationException>(
            () => new AgentRuntimeHost([new TestModule("invalid", "agent/duplicate", "agent/duplicate")]));
        Assert.Throws<InvalidOperationException>(
            () => host.RegisterModule(new TestModule("reserved", AgentRuntimeMethodNames.RequestCancel)));
    }

    [Fact]
    public async Task HostReturnsStableErrorsForInvalidAndUnsupportedRequests()
    {
        using var host = new AgentRuntimeHost([new TestModule("test", "agent/test")]);

        var missingId = await host.DispatchAsync("", "agent/test", default);
        var unsupported = await host.DispatchAsync("request-2", "agent/missing", default);
        var tooLong = await host.DispatchAsync(
            new string('x', AgentRuntimeContract.MaximumMethodCharacters + 1),
            "agent/test",
            default);

        Assert.False(missingId.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.InvalidRequest, missingId.ErrorCode);
        Assert.False(unsupported.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.UnsupportedMethod, unsupported.ErrorCode);
        Assert.False(tooLong.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.InvalidRequest, tooLong.ErrorCode);
    }

    [Fact]
    public async Task HostConvertsCancellationAndModuleFailuresToRuntimeErrors()
    {
        using var cancellationHost = new AgentRuntimeHost([new TestModule("cancel", "agent/cancel")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await cancellationHost.DispatchAsync(
            "cancel-1",
            "agent/cancel",
            default,
            cancellation.Token);

        var failureModule = new TestModule("failure", "agent/failure")
        {
            ThrowOnDispatch = true
        };
        using var failureHost = new AgentRuntimeHost([failureModule]);
        var failed = await failureHost.DispatchAsync("failure-1", "agent/failure", default);

        Assert.False(cancelled.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.Cancelled, cancelled.ErrorCode);
        Assert.False(failed.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.Internal, failed.ErrorCode);
        Assert.Contains("test failure", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostCancelsActiveRequestByRequestIdAndCleansItUp()
    {
        var module = new BlockingModule();
        using var host = new AgentRuntimeHost([module]);
        var dispatch = host.DispatchAsync(" active-1 ", "agent/block", default);

        await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["active-1"], host.ActiveRequestIds);
        Assert.True(host.TryCancelRequest(" active-1 "));

        var response = await dispatch;

        Assert.False(response.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.Cancelled, response.ErrorCode);
        Assert.Empty(host.ActiveRequestIds);
        Assert.False(host.TryCancelRequest("active-1"));
    }

    [Fact]
    public async Task HostCancelsActiveRequestThroughRuntimeMethod()
    {
        var module = new BlockingModule();
        using var host = new AgentRuntimeHost([module]);
        var dispatch = host.DispatchAsync("active-2", "agent/block", default);

        await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var parameters = JsonDocument.Parse("{\"requestId\":\"active-2\"}");

        var cancellation = await host.DispatchAsync(
            "cancel-2",
            AgentRuntimeMethodNames.RequestCancel,
            parameters.RootElement);

        Assert.True(cancellation.Ok);
        var result = cancellation.Result!.Value.Deserialize<AgentRuntimeRequestCancelResult>();
        Assert.NotNull(result);
        Assert.True(result!.Cancelled);
        Assert.Equal("active-2", result.RequestId);
        Assert.Equal(AgentRuntimeErrorCodes.Cancelled, (await dispatch).ErrorCode);
    }

    [Fact]
    public async Task HostRejectsDuplicateActiveRequestIds()
    {
        var module = new BlockingModule();
        using var host = new AgentRuntimeHost([module]);
        var first = host.DispatchAsync("duplicate-1", "agent/block", default);
        await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var duplicate = await host.DispatchAsync(" duplicate-1 ", "agent/block", default);

        Assert.False(duplicate.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.RequestInProgress, duplicate.ErrorCode);
        Assert.True(host.TryCancelRequest("duplicate-1"));
        Assert.Equal(AgentRuntimeErrorCodes.Cancelled, (await first).ErrorCode);
    }

    [Fact]
    public void DisposingHostDisposesRegisteredModulesAndStopsUse()
    {
        var module = new TestModule("test", "agent/test");
        var host = new AgentRuntimeHost([module]);

        host.Dispose();
        host.Dispose();

        Assert.Equal(1, module.DisposeCount);
        Assert.Throws<ObjectDisposedException>(
            () => host.Subscribe(_ => { }));
    }

    private sealed class TestModule : IAgentRuntimeModule, IDisposable
    {
        public TestModule(string name, params string[] methods)
        {
            Name = name;
            Methods = methods;
        }

        public string Name { get; }
        public IReadOnlyCollection<string> Methods { get; }
        public AgentRuntimeModuleContext? LastContext { get; private set; }
        public bool ThrowOnDispatch { get; init; }
        public int DisposeCount { get; private set; }

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            LastContext = context;
            if (ThrowOnDispatch)
                throw new InvalidOperationException("test failure");

            await context.EmitEventAsync("started", new { request.Method });
            await Task.Yield();
            context.CancellationToken.ThrowIfCancellationRequested();
            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class BlockingModule : IAgentRuntimeModule
    {
        public string Name => "blocking";
        public IReadOnlyCollection<string> Methods => ["agent/block"];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRuntimeResponse> DispatchAsync(
            AgentRuntimeRequest request,
            AgentRuntimeModuleContext context)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return new AgentRuntimeResponse
            {
                RequestId = request.RequestId,
                Ok = true
            };
        }
    }
}
