using System.Text.Json;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRuntimeSessionAdapterTests
{
    [Fact]
    public async Task InitializeAndPingFollowRuntimeRequestShape()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var initialize = await Dispatch(adapter, "1", AgentRuntimeMethodNames.Initialize);
        var ping = await Dispatch(adapter, "2", AgentRuntimeMethodNames.Ping);

        Assert.True(initialize.Ok);
        Assert.Equal("1", initialize.RequestId);
        Assert.Equal("cxshell-session-gateway", initialize.Result!.Value.GetProperty("runtime").GetString());
        Assert.Equal(AgentRuntimeContract.Protocol, initialize.Result.Value.GetProperty("protocol").GetString());
        Assert.Equal(AgentRuntimeContract.ProtocolVersion, initialize.Result.Value.GetProperty("protocolVersion").GetString());
        Assert.Contains(
            AgentRuntimeMethodNames.Initialize,
            initialize.Result.Value.GetProperty("methods").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "agent.session.list",
            initialize.Result.Value.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        Assert.True(ping.Ok);
        Assert.True(ping.Result!.Value.GetProperty("processId").GetInt32() > 0);
    }

    [Fact]
    public async Task InitializeAcceptsMatchingProtocolAndVersion()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(
            adapter,
            "initialize-negotiated-1",
            AgentRuntimeMethodNames.Initialize,
            new
            {
                protocol = AgentRuntimeContract.Protocol,
                protocolVersion = AgentRuntimeContract.ProtocolVersion
            });

        Assert.True(response.Ok);
        Assert.Equal(
            AgentRuntimeContract.Protocol,
            response.Result!.Value.GetProperty("protocol").GetString());
        Assert.Equal(
            AgentRuntimeContract.ProtocolVersion,
            response.Result.Value.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task InitializeRejectsProtocolAndVersionMismatches()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var protocol = await Dispatch(
            adapter,
            "initialize-mismatch-protocol",
            AgentRuntimeMethodNames.Initialize,
            new { protocol = "other-agent" });
        var version = await Dispatch(
            adapter,
            "initialize-mismatch-version",
            AgentRuntimeMethodNames.Initialize,
            new { protocolVersion = "999" });

        Assert.False(protocol.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.ProtocolMismatch, protocol.ErrorCode);
        Assert.False(version.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.ProtocolMismatch, version.ErrorCode);
    }

    [Fact]
    public async Task InitializeRejectsMalformedNegotiationParameters()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var scalar = await adapter.DispatchAsync(
            "initialize-invalid-shape",
            AgentRuntimeMethodNames.Initialize,
            JsonSerializer.SerializeToElement("invalid"));
        var blankProtocol = await Dispatch(
            adapter,
            "initialize-invalid-protocol",
            AgentRuntimeMethodNames.Initialize,
            new { protocol = " " });

        Assert.False(scalar.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.InvalidParameters, scalar.ErrorCode);
        Assert.False(blankProtocol.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.InvalidParameters, blankProtocol.ErrorCode);
    }

    [Fact]
    public async Task RuntimeErrorsExposeStableMachineReadableCodes()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        using var adapter = new AgentRuntimeSessionAdapter(gateway);

        var invalidRequest = await adapter.DispatchAsync(
            "",
            AgentRuntimeMethodNames.Ping,
            JsonSerializer.SerializeToElement(new { }));
        var invalidParameters = await Dispatch(
            adapter,
            "invalid-parameters-1",
            AgentRuntimeMethodNames.SessionCommand,
            new { });
        var unsupported = await Dispatch(adapter, "unsupported-1", "agent/unknown");

        Assert.False(invalidRequest.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.InvalidRequest, invalidRequest.ErrorCode);
        Assert.False(invalidParameters.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.InvalidParameters, invalidParameters.ErrorCode);
        Assert.False(unsupported.Ok);
        Assert.Equal(AgentRuntimeErrorCodes.UnsupportedMethod, unsupported.ErrorCode);
    }

    [Fact]
    public async Task RuntimeInfoDescribesTheStableContract()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(adapter, "runtime-info-1", AgentRuntimeMethodNames.RuntimeInfo);

        Assert.True(response.Ok);
        var result = response.Result!.Value;
        Assert.Equal("cxshell-agent", result.GetProperty("protocol").GetString());
        Assert.Equal("1", result.GetProperty("protocolVersion").GetString());
        Assert.Contains(
            AgentRuntimeMethodNames.RuntimeInfo,
            result.GetProperty("methods").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "agent.audit.read",
            result.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "SSH",
            result.GetProperty("supportedProtocols").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task ToolCatalogExposesSchemasAndCurrentAvailability()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(adapter, "tool-catalog-1", AgentRuntimeMethodNames.ToolCatalog);

        Assert.True(response.Ok);
        var result = response.Result!.Value;
        var tools = result.GetProperty("tools").EnumerateArray().ToList();
        Assert.Contains(
            AgentRunCoordinator.SessionCommandToolName,
            tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.Contains(
            AgentReadOnlyToolCatalog.LogsToolName,
            tools.Select(tool => tool.GetProperty("name").GetString()));

        var sessionCommand = tools.Single(tool =>
            tool.GetProperty("name").GetString() == AgentRunCoordinator.SessionCommandToolName);
        Assert.True(sessionCommand.GetProperty("available").GetBoolean());
        Assert.Equal(
            "object",
            sessionCommand.GetProperty("parameters").GetProperty("type").GetString());

        var diagnostics = tools.Single(tool =>
            tool.GetProperty("name").GetString() == AgentRunCoordinator.DiagnosticRunToolName);
        Assert.False(diagnostics.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.GetProperty("unavailableReason").GetString()));
        Assert.True(result.GetProperty("requiresApprovalForDangerousCommands").GetBoolean());
        Assert.False(result.GetProperty("requiresApprovalForChangeCommands").GetBoolean());
    }

    [Fact]
    public async Task CapabilityNamesAreComparedCaseInsensitively()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(
            adapter,
            "capability-case-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = " AGENT.AUDIT.READ " });

        Assert.True(response.Ok);
        Assert.True(response.Result!.Value.GetProperty("supported").GetBoolean());
        Assert.Equal("agent.audit.read", response.Result.Value.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task RuntimeRejectsOversizedCommandAndMessagePayloads()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var oversizedCommand = await Dispatch(
            adapter,
            "oversized-command-1",
            AgentRuntimeMethodNames.SessionCommand,
            new
            {
                sessionId = snapshot.SessionId.ToString("D"),
                command = new string('x', AgentSessionGateway.MaximumCommandLength + 1)
            });
        var oversizedMessages = Enumerable.Range(0, AgentRuntimeContract.MaximumMessageCount + 1)
            .Select(_ => new { role = "user", content = "hello" })
            .ToArray();
        var oversizedPayload = await Dispatch(
            adapter,
            "oversized-messages-1",
            AgentRuntimeMethodNames.Run,
            new
            {
                sessionId = snapshot.SessionId.ToString("D"),
                messages = oversizedMessages
            });

        Assert.False(oversizedCommand.Ok);
        Assert.Contains("Command length", oversizedCommand.Error, StringComparison.Ordinal);
        Assert.False(oversizedPayload.Ok);
        Assert.Contains("messages array", oversizedPayload.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityCheckClaimsAgentRunSupport()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(
            adapter,
            "capability-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.run" });

        Assert.True(response.Ok);
        Assert.True(response.Result!.Value.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.Result.Value.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task CapabilityCheckClaimsReadOnlyRunbookSupport()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult("diagnostic output"));
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => [endpoint]));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(
            adapter,
            "runbook-capability-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.diagnostic.runbook" });

        Assert.True(response.Ok);
        Assert.True(response.Result!.Value.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task CapabilityCheckClaimsAuditAndRunListSupport()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var audit = await Dispatch(
            adapter,
            "audit-capability-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.audit.read" });
        var runs = await Dispatch(
            adapter,
            "run-list-capability-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.run.list" });
        var events = await Dispatch(
            adapter,
            "run-events-capability-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.run.events" });
        var status = await Dispatch(
            adapter,
            "run-status-capability-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.run.status" });

        Assert.True(audit.Ok);
        Assert.True(audit.Result!.Value.GetProperty("supported").GetBoolean());
        Assert.True(runs.Ok);
        Assert.True(runs.Result!.Value.GetProperty("supported").GetBoolean());
        Assert.True(events.Ok);
        Assert.True(events.Result!.Value.GetProperty("supported").GetBoolean());
        Assert.True(status.Ok);
        Assert.True(status.Result!.Value.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task CapabilityCheckReflectsDisabledCommandExecutionPolicy()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask);
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => [endpoint]),
            new AgentPermissionPolicy { AllowCommandExecution = false });
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(
            adapter,
            "capability-policy-1",
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability = "agent.session.command.execute" });

        Assert.True(response.Ok);
        Assert.False(response.Result!.Value.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task RunAppendAndStopUseTheCoordinatorBoundary()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var provider = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "test-provider",
            BaseUrl = "https://provider.example",
            Model = "test-model",
            RequiresApiKey = false
        };
        var coordinator = new RecordingRunCoordinator();
        using var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => provider,
            runCoordinator: coordinator);

        var append = await Dispatch(
            adapter,
            "run-append-1",
            AgentRuntimeMethodNames.RunAppend,
            new
            {
                runId = "active-run",
                messages = new[] { new { role = "user", content = "continue the check" } }
            });
        var stop = await Dispatch(
            adapter,
            "run-stop-1",
            AgentRuntimeMethodNames.RunStop,
            new { runId = "active-run" });

        Assert.True(append.Ok);
        Assert.True(append.Result!.Value.GetProperty("appended").GetBoolean());
        Assert.Equal(1, append.Result.Value.GetProperty("messageCount").GetInt32());
        Assert.Equal("continue the check", coordinator.AppendedMessages.Single().Content);
        Assert.True(stop.Ok);
        Assert.True(stop.Result!.Value.GetProperty("requested").GetBoolean());
        Assert.Equal("active-run", coordinator.StoppedRunId);
    }

    [Fact]
    public async Task SessionListAndGetUseGatewaySnapshots()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var list = await Dispatch(adapter, "list-1", AgentRuntimeMethodNames.SessionList);
        var get = await Dispatch(
            adapter,
            "get-1",
            AgentRuntimeMethodNames.SessionGet,
            new { sessionId = snapshot.SessionId.ToString("D") });

        Assert.True(list.Ok);
        Assert.Equal(1, list.Result!.Value.GetProperty("sessions").GetArrayLength());
        Assert.True(get.Ok);
        Assert.True(get.Result!.Value.GetProperty("found").GetBoolean());
        Assert.Equal(
            snapshot.SessionId.ToString("D"),
            get.Result.Value.GetProperty("session").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task SavedSessionLifecycleIsExposedThroughRuntimeWithoutSecrets()
    {
        var savedSession = new AgentSavedSessionSnapshot
        {
            SavedSessionId = Guid.NewGuid(),
            Name = "Runtime saved SSH",
            Path = "Operations/Runtime saved SSH",
            Protocol = SessionProtocol.SSH,
            Host = "runtime-saved.example",
            Port = 22,
            Username = "operator"
        };
        var runtimeSession = CreateSnapshot(isConnected: true);
        var closedSessionId = Guid.Empty;
        using var gateway = new AgentSessionGateway(new DelegateAgentSessionHost(
            () => [],
            _ => Task.FromResult<IReadOnlyList<AgentSavedSessionSnapshot>>([savedSession]),
            (_, _) => Task.FromResult(new AgentSessionOpenResult(
                AgentSessionOpenStatus.Opened,
                runtimeSession,
                AgentOwned: true)),
            (sessionId, _) =>
            {
                closedSessionId = sessionId;
                return Task.FromResult(new AgentSessionCloseResult(AgentSessionCloseStatus.Closed));
            }));
        using var adapter = new AgentRuntimeSessionAdapter(gateway);

        var list = await Dispatch(adapter, "saved-list-1", AgentRuntimeMethodNames.SavedSessionList);
        var open = await Dispatch(
            adapter,
            "saved-open-1",
            AgentRuntimeMethodNames.SavedSessionOpen,
            new
            {
                savedSessionId = savedSession.SavedSessionId.ToString("D"),
                reason = "inspect the host"
            });
        var close = await Dispatch(
            adapter,
            "saved-close-1",
            AgentRuntimeMethodNames.SavedSessionClose,
            new { sessionId = runtimeSession.SessionId.ToString("D") });

        Assert.True(list.Ok);
        Assert.DoesNotContain("password", list.Result!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(list.Result.Value.GetProperty("sessions").EnumerateArray());
        Assert.True(open.Ok);
        Assert.True(open.Result!.Value.GetProperty("opened").GetBoolean());
        Assert.True(open.Result.Value.GetProperty("agentOwned").GetBoolean());
        Assert.True(close.Ok);
        Assert.True(close.Result!.Value.GetProperty("closed").GetBoolean());
        Assert.Equal(runtimeSession.SessionId, closedSessionId);
    }

    [Fact]
    public async Task AuditListReturnsRecentEntriesWithoutRawCommand()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        var command = "printf 'do not expose this command'";
        using var gateway = CreateGateway(
            snapshot,
            (_, _) => Task.CompletedTask);
        var executed = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = command
        });
        Assert.Equal(AgentCommandStatus.Sent, executed.Status);

        using var adapter = new AgentRuntimeSessionAdapter(gateway);
        var response = await Dispatch(
            adapter,
            "audit-list-1",
            AgentRuntimeMethodNames.AuditList,
            new { limit = 9999 });

        Assert.True(response.Ok);
        Assert.Equal(AgentAuditLog.MaximumEntries, response.Result!.Value
            .GetProperty("limit").GetInt32());
        var entry = response.Result.Value.GetProperty("entries").EnumerateArray().First();
        Assert.Equal(AgentCommandStatus.Sent.ToString(), entry.GetProperty("status").GetString());
        Assert.Equal(command.Length, entry.GetProperty("commandLength").GetInt32());
        Assert.NotEqual(command, entry.GetProperty("commandFingerprint").GetString());
        Assert.DoesNotContain(command, response.Result.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunListReturnsAnActiveRun()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "test-provider",
            BaseUrl = "https://provider.example",
            Model = "test-model",
            RequiresApiKey = false
        };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new BlockingModelClient(started));
        using var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => provider,
            runCoordinator: coordinator);

        var start = await Dispatch(
            adapter,
            "run-start-1",
            AgentRuntimeMethodNames.Run,
            new
            {
                runId = "active-run",
                sessionId = snapshot.SessionId.ToString("D"),
                messages = new[] { new { role = "user", content = "wait" } }
            });
        Assert.True(start.Ok);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var list = await Dispatch(adapter, "run-list-1", AgentRuntimeMethodNames.RunList);

        Assert.True(list.Ok);
        var run = Assert.Single(list.Result!.Value.GetProperty("runs").EnumerateArray());
        Assert.Equal("active-run", run.GetProperty("runId").GetString());
        Assert.Equal(snapshot.SessionId.ToString("D"), run.GetProperty("sessionId").GetString());
        Assert.Equal("running", run.GetProperty("status").GetString());

        var cancel = await Dispatch(
            adapter,
            "run-cancel-1",
            AgentRuntimeMethodNames.Cancel,
            new { runId = "active-run" });
        Assert.True(cancel.Ok);
        Assert.True(cancel.Result!.Value.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task RunEventsReturnsCompletedHistoryAndValidatesCursor()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "test-provider",
            BaseUrl = "https://provider.example",
            Model = "test-model",
            RequiresApiKey = false
        };
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new ImmediateModelClient(provider));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coordinator.Subscribe(envelope =>
        {
            if (envelope.Events.Any(@event => @event.Type == "loop_end"))
                completed.TrySetResult(true);
        });
        using var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => provider,
            runCoordinator: coordinator);

        var start = await Dispatch(
            adapter,
            "run-events-start-1",
            AgentRuntimeMethodNames.Run,
            new
            {
                runId = "runtime-events-run",
                sessionId = snapshot.SessionId.ToString("D"),
                messages = new[] { new { role = "user", content = "check" } }
            });
        Assert.True(start.Ok);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var first = await Dispatch(
            adapter,
            "run-events-read-1",
            AgentRuntimeMethodNames.RunEvents,
            new { runId = "runtime-events-run", limit = 1 });
        Assert.True(first.Ok);
        Assert.Equal(1, first.Result!.Value.GetProperty("events").GetArrayLength());
        Assert.Equal(1, first.Result.Value.GetProperty("nextSequence").GetInt64());
        Assert.True(first.Result.Value.GetProperty("hasMore").GetBoolean());
        Assert.False(first.Result.Value.GetProperty("hasGap").GetBoolean());

        var status = await Dispatch(
            adapter,
            "run-status-read-1",
            AgentRuntimeMethodNames.RunStatus,
            new { runId = "runtime-events-run" });
        Assert.True(status.Ok);
        Assert.True(status.Result!.Value.GetProperty("found").GetBoolean());
        Assert.Equal(
            "completed",
            status.Result.Value.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(
            "completed",
            status.Result.Value.GetProperty("run").GetProperty("endReason").GetString());
        Assert.Equal(
            7,
            status.Result.Value.GetProperty("run").GetProperty("eventCount").GetInt64());

        var rest = await Dispatch(
            adapter,
            "run-events-read-2",
            AgentRuntimeMethodNames.RunEvents,
            new { runId = "runtime-events-run", afterSequence = 1, limit = 9999 });
        Assert.True(rest.Ok);
        Assert.Equal(6, rest.Result!.Value.GetProperty("events").GetArrayLength());
        Assert.False(rest.Result.Value.GetProperty("hasMore").GetBoolean());
        Assert.False(rest.Result.Value.GetProperty("hasGap").GetBoolean());

        var negative = await Dispatch(
            adapter,
            "run-events-negative-1",
            AgentRuntimeMethodNames.RunEvents,
            new { runId = "runtime-events-run", afterSequence = -1 });
        var missing = await Dispatch(
            adapter,
            "run-events-missing-1",
            AgentRuntimeMethodNames.RunEvents,
            new { runId = "missing-runtime-run" });

        Assert.False(negative.Ok);
        Assert.Contains("cannot be negative", negative.Error, StringComparison.Ordinal);
        Assert.False(missing.Ok);
        Assert.Contains("event history", missing.Error, StringComparison.Ordinal);

        var unknownStatus = await Dispatch(
            adapter,
            "run-status-missing-1",
            AgentRuntimeMethodNames.RunStatus,
            new { runId = "missing-runtime-run" });
        Assert.True(unknownStatus.Ok);
        Assert.False(unknownStatus.Result!.Value.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task FleetDiagnosticReturnsAggregatedSessionResults()
    {
        var linux = CreateSnapshot(isConnected: true) with
        {
            Host = "linux.runtime.test",
            Platform = "Linux/Unix"
        };
        var windows = CreateSnapshot(isConnected: true) with
        {
            Host = "windows.runtime.test",
            Platform = "Windows"
        };
        var endpoints = new IAgentSessionEndpoint[]
        {
            new AgentSessionEndpoint(
                () => linux,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult("linux runtime output")),
            new AgentSessionEndpoint(
                () => windows,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult("windows runtime output"))
        };
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => endpoints));
        using var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(
            adapter,
            "fleet-1",
            AgentRuntimeMethodNames.FleetDiagnostic,
            new { scope = "system" });

        Assert.True(response.Ok);
        Assert.Equal("system", response.Result!.Value.GetProperty("scope").GetString());
        Assert.Equal(2, response.Result.Value.GetProperty("targetCount").GetInt32());
        Assert.Equal(2, response.Result.Value.GetProperty("successCount").GetInt32());
        Assert.Equal(2, response.Result.Value.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task SessionCommandRoutesJsonParametersToGateway()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        AgentCommandRequest? sentRequest = null;
        using var gateway = CreateGateway(snapshot, (request, _) =>
        {
            sentRequest = request;
            return Task.CompletedTask;
        });
        var adapter = new AgentRuntimeSessionAdapter(gateway);
        var commandRequestId = Guid.NewGuid();

        var response = await Dispatch(
            adapter,
            "command-1",
            AgentRuntimeMethodNames.SessionCommand,
            new
            {
                sessionId = snapshot.SessionId.ToString("D"),
                requestId = commandRequestId.ToString("D"),
                command = "whoami",
                timeoutMs = 5_000,
                appendLineEnding = false
            });

        Assert.True(response.Ok);
        Assert.Equal(AgentCommandStatus.Sent.ToString(), response.Result!.Value
            .GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(commandRequestId, sentRequest?.RequestId);
        Assert.Equal("whoami", sentRequest?.Command);
        Assert.False(sentRequest?.AppendLineEnding);
    }

    [Fact]
    public async Task SessionCommandApprovalReturnsSingleUseToken()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        using var adapter = new AgentRuntimeSessionAdapter(gateway);
        var commandRequestId = Guid.NewGuid();

        var denied = await Dispatch(
            adapter,
            "dangerous-1",
            AgentRuntimeMethodNames.SessionCommand,
            new
            {
                sessionId = snapshot.SessionId.ToString("D"),
                requestId = commandRequestId.ToString("D"),
                command = "sudo reboot"
            });

        Assert.True(denied.Ok);
        Assert.True(denied.Result!.Value.GetProperty("result").GetProperty("approvalRequired").GetBoolean());

        var approved = await Dispatch(
            adapter,
            "approve-1",
            AgentRuntimeMethodNames.SessionCommandApprove,
            new { requestId = commandRequestId.ToString("D") });

        Assert.True(approved.Ok);
        Assert.True(approved.Result!.Value.GetProperty("approved").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            approved.Result.Value.GetProperty("approvalToken").GetString()));
    }

    [Fact]
    public async Task RunReturnsAcceptedIdAndCanBeCancelledThroughRunProtocol()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "test-provider",
            BaseUrl = "https://provider.example",
            Model = "test-model",
            RequiresApiKey = false
        };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new BlockingModelClient(started));
        using var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => provider,
            runCoordinator: coordinator);

        var response = await Dispatch(
            adapter,
            "run-1",
            AgentRuntimeMethodNames.Run,
            new
            {
                runId = "adapter-run",
                sessionId = snapshot.SessionId.ToString("D"),
                messages = new[] { new { role = "user", content = "wait" } }
            });

        Assert.True(response.Ok);
        Assert.True(response.Result!.Value.GetProperty("started").GetBoolean());
        Assert.Equal("adapter-run", response.Result.Value.GetProperty("runId").GetString());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancel = await Dispatch(
            adapter,
            "cancel-1",
            AgentRuntimeMethodNames.Cancel,
            new { runId = "adapter-run" });

        Assert.True(cancel.Ok);
        Assert.True(cancel.Result!.Value.GetProperty("cancelled").GetBoolean());
        Assert.Equal("adapter-run", cancel.Result.Value.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task RunApprovalMethodsRouteThroughRuntimeContract()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var coordinator = new RecordingRunCoordinator();
        using var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            runCoordinator: coordinator);

        var approved = await Dispatch(
            adapter,
            "run-approve-1",
            AgentRuntimeMethodNames.RunApprove,
            new { runId = "runtime-run", toolCallId = "tool-approve" });
        var denied = await Dispatch(
            adapter,
            "run-deny-1",
            AgentRuntimeMethodNames.RunDeny,
            new { runId = "runtime-run", toolCallId = "tool-deny" });

        Assert.True(approved.Ok);
        Assert.True(approved.Result!.Value.GetProperty("decided").GetBoolean());
        Assert.True(approved.Result.Value.GetProperty("approved").GetBoolean());
        Assert.Equal("runtime-run", approved.Result.Value.GetProperty("runId").GetString());
        Assert.Equal("tool-approve", approved.Result.Value.GetProperty("toolCallId").GetString());
        Assert.Equal(("runtime-run", "tool-approve"), coordinator.Approved);

        Assert.True(denied.Ok);
        Assert.True(denied.Result!.Value.GetProperty("decided").GetBoolean());
        Assert.False(denied.Result.Value.GetProperty("approved").GetBoolean());
        Assert.Equal("runtime-run", denied.Result.Value.GetProperty("runId").GetString());
        Assert.Equal("tool-deny", denied.Result.Value.GetProperty("toolCallId").GetString());
        Assert.Equal(("runtime-run", "tool-deny"), coordinator.Denied);
    }

    [Fact]
    public async Task HostForwardsBackgroundRunEventsFromSessionAdapter()
    {
        var snapshot = CreateSnapshot(isConnected: true);
        using var gateway = CreateGateway(snapshot);
        var provider = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "test-provider",
            BaseUrl = "https://provider.example",
            Model = "test-model",
            RequiresApiKey = false
        };
        using var coordinator = new AgentRunCoordinator(
            gateway,
            () => provider,
            new ImmediateModelClient(provider));
        var forwarded = new TaskCompletionSource<AgentRuntimeModuleEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new AgentRuntimeSessionAdapter(
            gateway,
            () => provider,
            runCoordinator: coordinator);
        using var host = new AgentRuntimeHost([adapter]);
        using var subscription = host.Subscribe(@event =>
        {
            if (@event.EventName == "run" &&
                @event.RequestId == "host-forwarded-run")
            {
                forwarded.TrySetResult(@event);
            }
        });

        var response = await host.DispatchAsync(
            new AgentRuntimeRequest(
                "start-run-request",
                AgentRuntimeMethodNames.Run,
                JsonSerializer.SerializeToElement(new
                {
                    runId = "host-forwarded-run",
                    sessionId = snapshot.SessionId.ToString("D"),
                    messages = new[] { new { role = "user", content = "check" } }
                })));

        Assert.True(response.Ok);
        var @event = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentRuntimeMethodNames.Run, @event.Method);
        var envelope = Assert.IsType<AgentRuntimeStreamEnvelope>(@event.Payload);
        Assert.Equal("host-forwarded-run", envelope.RunId);
        Assert.Contains(
            envelope.Events,
            item => item.Type is "run_start" or "text_delta" or "loop_end");
    }

    [Fact]
    public async Task UnknownMethodReturnsProtocolError()
    {
        using var gateway = CreateGateway(CreateSnapshot(isConnected: true));
        var adapter = new AgentRuntimeSessionAdapter(gateway);

        var response = await Dispatch(adapter, "unknown-1", "agent/unknown");

        Assert.False(response.Ok);
        Assert.Contains("Unsupported method", response.Error);
    }

    private static Task<AgentRuntimeResponse> Dispatch(
        IAgentRuntimeSessionAdapter adapter,
        string requestId,
        string method,
        object? parameters = null)
    {
        var element = parameters == null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(parameters);
        return adapter.DispatchAsync(requestId, method, element);
    }

    private static AgentSessionGateway CreateGateway(
        AgentSessionSnapshot snapshot,
        Func<AgentCommandRequest, CancellationToken, Task>? send = null)
    {
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            send ?? ((_, _) => Task.CompletedTask));
        var host = new DelegateAgentSessionHost(() => [endpoint]);
        return new AgentSessionGateway(host);
    }

    private static AgentSessionSnapshot CreateSnapshot(bool isConnected)
    {
        var session = new SessionInfo
        {
            Name = "Runtime test session",
            Host = "runtime.example",
            Username = "operator",
            Protocol = SessionProtocol.SSH
        };
        return AgentSessionSnapshot.FromSession(session, isConnected);
    }

    private sealed class BlockingModelClient : IAgentModelClient
    {
        private readonly TaskCompletionSource<bool> _started;

        public BlockingModelClient(TaskCompletionSource<bool> started)
        {
            _started = started;
        }

        public async Task<AgentModelResponse> CompleteAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AgentModelResponse("never", provider.Model, provider.BuiltinId);
        }
    }

    private sealed class ImmediateModelClient : IAgentModelClient
    {
        private readonly AgentProviderSettings _provider;

        public ImmediateModelClient(AgentProviderSettings provider)
        {
            _provider = provider;
        }

        public Task<AgentModelResponse> CompleteAsync(
            AgentProviderSettings provider,
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentModelResponse(
                "server is healthy",
                _provider.Model,
                _provider.BuiltinId));
    }

    private sealed class RecordingRunCoordinator : IAgentRunCoordinator
    {
        public (string RunId, string ToolCallId)? Approved { get; private set; }
        public (string RunId, string ToolCallId)? Denied { get; private set; }
        public IReadOnlyList<AgentChatMessage> AppendedMessages { get; private set; } = [];
        public string? StoppedRunId { get; private set; }

        public AgentRunStartResult Start(AgentRunRequest request)
            => new(true, request.RunId ?? "recorded-run");

        public AgentRunCancellationResult Cancel(string runId)
            => new(true, runId);

        public AgentRunAppendMessagesResult AppendMessages(
            string runId,
            IReadOnlyList<AgentChatMessage> messages)
        {
            AppendedMessages = messages;
            return new(true, runId, messages.Count);
        }

        public AgentRunStopResult RequestStop(string runId)
        {
            StoppedRunId = runId;
            return new(true, runId);
        }

        public AgentRunApprovalResult Approve(string runId, string toolCallId)
        {
            Approved = (runId, toolCallId);
            return new(true, true, runId, toolCallId);
        }

        public AgentRunApprovalResult Deny(string runId, string toolCallId)
        {
            Denied = (runId, toolCallId);
            return new(true, false, runId, toolCallId);
        }

        public IReadOnlyList<AgentRuntimeRunSnapshot> GetActiveRuns() => [];

        public IReadOnlyList<AgentRuntimeRunSnapshot> GetRecentRuns(
            int limit = AgentRunCoordinator.DefaultRunListLimit)
            => [];

        public AgentRuntimeRunSnapshot? GetRun(string runId) => null;

        public int ClearCompletedRuns() => 0;

        public AgentRuntimeRunEventsResult? ReadEvents(
            string runId,
            long afterSequence = 0,
            int limit = AgentRunCoordinator.DefaultEventReadLimit)
            => null;

        public IDisposable Subscribe(Action<AgentRuntimeStreamEnvelope> observer)
            => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
