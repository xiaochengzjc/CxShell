using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentSessionGatewayTests
{
    [Fact]
    public void GetSessionsExposesConnectedAndDisconnectedSshWithoutSecrets()
    {
        var ssh = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var telnet = CreateSnapshot(SessionProtocol.TELNET, isConnected: true);
        var gateway = CreateGateway(ssh, telnet);

        var sessions = gateway.GetSessions();

        var result = Assert.Single(sessions);
        Assert.Equal(ssh.SessionId, result.SessionId);
        Assert.Equal(SessionProtocol.SSH, result.Protocol);
        Assert.True(result.CanExecuteCommands);
        Assert.DoesNotContain("password", result.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", result.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteCommandDispatchesToEndpointAndRecordsNoRawCommand()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        AgentCommandRequest? sentRequest = null;
        var gateway = CreateGateway(snapshot, (request, _) =>
        {
            sentRequest = request;
            return Task.CompletedTask;
        });
        var request = new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "printf 'secret-value'"
        };

        var result = await gateway.ExecuteCommandAsync(request);

        Assert.Equal(AgentCommandStatus.Sent, result.Status);
        Assert.Equal(AgentCommandExecutionState.Dispatched, result.ExecutionState);
        Assert.False(result.IsRetrySafe);
        Assert.Same(request, sentRequest);
        var audit = Assert.Single(gateway.ReadAudit());
        Assert.DoesNotContain(request.Command, audit.Detail, StringComparison.Ordinal);
        Assert.NotEqual(request.Command, audit.CommandFingerprint);
        Assert.Equal(request.Command.Length, audit.CommandLength);
    }

    [Fact]
    public async Task ExecuteCommandCanCaptureOutputThroughAnEndpointRunner()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask,
            (request, _) => Task.FromResult($"output for {request.Command}"));
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => [endpoint]));

        var result = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "uname -a"
        });

        Assert.True(gateway.Capabilities.SupportsCommandOutputCapture);
        Assert.True(gateway.Capabilities.SupportsReadOnlyDiagnostics);
        Assert.Equal(AgentCommandStatus.Sent, result.Status);
        Assert.True(result.RemoteCompletionConfirmed);
        Assert.Equal(AgentCommandExecutionState.Completed, result.ExecutionState);
        Assert.Equal("output for uname -a", result.Output);
    }

    [Fact]
    public async Task CapturedRemoteFailurePreservesExitCodeStdoutAndStderr()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask,
            runCommand: null,
            runCommandResult: (_, _) => Task.FromResult(
                new AgentCommandExecutionResult(
                    RemoteCompletionConfirmed: true,
                    Output: "partial output",
                    Error: "permission denied",
                    ExitCode: 13)));
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => [endpoint]));

        var result = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "apt-get install nginx"
        });

        Assert.Equal(AgentCommandStatus.Failed, result.Status);
        Assert.Equal(AgentCommandExecutionState.Failed, result.ExecutionState);
        Assert.True(result.RemoteCompletionConfirmed);
        Assert.Equal(13, result.ExitCode);
        Assert.Equal("partial output", result.Output);
        Assert.Equal("permission denied", result.Error);
        Assert.Contains("exit code 13", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsOutcomeCertain);
    }

    [Fact]
    public async Task ReusingACompletedRequestIdReturnsTheCachedResultWithoutDispatchingAgain()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var calls = 0;
        using var gateway = CreateGateway(snapshot, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var request = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = snapshot.SessionId,
            Command = "uname -a"
        };

        var first = await gateway.ExecuteCommandAsync(request);
        var second = await gateway.ExecuteCommandAsync(request);

        Assert.Equal(AgentCommandStatus.Sent, first.Status);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(first.CompletedAtUtc, second.CompletedAtUtc);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReusingARequestIdForAnotherCommandIsRejected()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var calls = 0;
        using var gateway = CreateGateway(snapshot, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var request = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = snapshot.SessionId,
            Command = "pwd"
        };

        await gateway.ExecuteCommandAsync(request);
        var result = await gateway.ExecuteCommandAsync(request with { Command = "whoami" });

        Assert.Equal(AgentCommandStatus.InvalidRequest, result.Status);
        Assert.Contains("different command", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FleetDiagnosticOnlyInspectsConnectedSshAndAggregatesResults()
    {
        var linux = CreateSnapshot(SessionProtocol.SSH, isConnected: true) with
        {
            Name = "Linux host",
            Host = "linux.example",
            Platform = "Linux/Unix"
        };
        var windows = CreateSnapshot(SessionProtocol.SSH, isConnected: true) with
        {
            Name = "Windows host",
            Host = "windows.example",
            Platform = "Windows"
        };
        var disconnected = CreateSnapshot(SessionProtocol.SSH, isConnected: false) with
        {
            Name = "Disconnected host",
            Platform = "Linux/Unix"
        };
        var rdp = CreateSnapshot(SessionProtocol.RDP, isConnected: true) with
        {
            Name = "RDP host",
            Platform = "Windows"
        };
        var requests = new System.Collections.Concurrent.ConcurrentBag<AgentCommandRequest>();
        var endpoints = new IAgentSessionEndpoint[]
        {
            new AgentSessionEndpoint(
                () => linux,
                (_, _) => Task.CompletedTask,
                (request, _) =>
                {
                    requests.Add(request);
                    return Task.FromResult("linux disk output");
                }),
            new AgentSessionEndpoint(
                () => windows,
                (_, _) => Task.CompletedTask,
                (request, _) =>
                {
                    requests.Add(request);
                    return Task.FromException<string>(new InvalidOperationException("windows runner failed"));
                }),
            new AgentSessionEndpoint(
                () => disconnected,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult("must not run")),
            new AgentSessionEndpoint(
                () => rdp,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult("must not run"))
        };
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => endpoints));

        var result = await gateway.RunReadOnlyDiagnosticAcrossSessionsAsync(" disk ");

        Assert.Equal("disk", result.Scope);
        Assert.Equal(2, result.TargetCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(
            ["Linux host", "Windows host"],
            result.Results.Select(item => item.Name));
        Assert.Equal(AgentCommandStatus.Sent.ToString(), result.Results[0].Status);
        Assert.Equal(AgentCommandStatus.Failed.ToString(), result.Results[1].Status);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
            Assert.Equal("fleet diagnostic disk", request.DisplayCommand));

        var linuxRequest = requests.Single(request => request.SessionId == linux.SessionId);
        Assert.Contains("df -P -h", linuxRequest.Command, StringComparison.OrdinalIgnoreCase);

        var windowsRequest = requests.Single(request => request.SessionId == windows.SessionId);
        Assert.StartsWith("powershell.exe -NoProfile -NonInteractive", windowsRequest.Command, StringComparison.Ordinal);
        var encodedScript = windowsRequest.Command[(windowsRequest.Command.LastIndexOf(' ') + 1)..];
        var windowsScript = System.Text.Encoding.Unicode.GetString(
            Convert.FromBase64String(encodedScript));
        Assert.Contains("Win32_LogicalDisk", windowsScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetDiagnosticCancellationStopsTheWholeInspection()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true) with
        {
            Platform = "Linux/Unix"
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = new AgentSessionEndpoint(
            () => snapshot,
            (_, _) => Task.CompletedTask,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() => [endpoint]));
        using var cancellation = new CancellationTokenSource();

        var inspection = gateway.RunReadOnlyDiagnosticAcrossSessionsAsync(
            AgentDiagnosticCatalog.SystemScope,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inspection);
    }

    [Fact]
    public async Task FleetDiagnosticRejectsUnknownScopeBeforeEnumeratingEndpoints()
    {
        var enumerated = false;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            {
                enumerated = true;
                return [];
            }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.RunReadOnlyDiagnosticAcrossSessionsAsync("unknown"));
        Assert.False(enumerated);
    }

    [Fact]
    public async Task DangerousCommandIsDeniedBeforeEndpointIsCalled()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var called = false;
        var gateway = CreateGateway(snapshot, (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var result = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "sudo rm -rf /tmp/example"
        });

        Assert.Equal(AgentCommandStatus.Denied, result.Status);
        Assert.False(called);
        var audit = Assert.Single(gateway.ReadAudit());
        Assert.Equal(AgentCommandRisk.Dangerous, audit.Risk);
        Assert.Equal(AgentPermissionDecision.DangerousCommand, audit.PermissionDecision);
        Assert.True(audit.ApprovalRequired);
    }

    [Fact]
    public async Task DangerousCommandRunsOnlyAfterOneTimeApproval()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var called = 0;
        using var gateway = CreateGateway(snapshot, (request, _) =>
        {
            called++;
            return Task.CompletedTask;
        });
        var request = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = snapshot.SessionId,
            Command = "sudo rm -rf /tmp/example"
        };

        var denied = await gateway.ExecuteCommandAsync(request);

        Assert.Equal(AgentCommandStatus.Denied, denied.Status);
        Assert.True(denied.ApprovalRequired);
        Assert.True(gateway.TryApprove(request.RequestId, out var token));
        Assert.NotEmpty(token);

        var approved = await gateway.ExecuteCommandAsync(request with { ApprovalToken = token });

        Assert.Equal(AgentCommandStatus.Sent, approved.Status);
        Assert.False(approved.ApprovalRequired);
        Assert.Equal(1, called);
        Assert.False(gateway.TryApprove(request.RequestId, out _));
        var auditEntries = gateway.ReadAudit(2);
        Assert.Equal(2, auditEntries.Count);
        Assert.Contains(auditEntries, entry => entry.ApprovalGranted);
    }

    [Fact]
    public async Task ModifyingCommandRunsOnlyAfterApprovalWhenPolicyRequiresIt()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var called = 0;
        using var gateway = new AgentSessionGateway(
            new DelegateAgentSessionHost(() =>
            [
                new AgentSessionEndpoint(
                    () => snapshot,
                    (_, _) =>
                    {
                        called++;
                        return Task.CompletedTask;
                    })
            ]),
            new AgentPermissionPolicy { RequireApprovalForChangeCommands = true });
        var request = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = snapshot.SessionId,
            Command = "systemctl restart sshd"
        };

        var denied = await gateway.ExecuteCommandAsync(request);

        Assert.Equal(AgentCommandStatus.Denied, denied.Status);
        Assert.True(denied.ApprovalRequired);
        Assert.True(gateway.TryApprove(request.RequestId, out var token));

        var approved = await gateway.ExecuteCommandAsync(request with { ApprovalToken = token });

        Assert.Equal(AgentCommandStatus.Sent, approved.Status);
        Assert.Equal(1, called);
    }

    [Fact]
    public async Task ApprovalCannotBeReusedForAnotherCommand()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var called = 0;
        using var gateway = CreateGateway(snapshot, (_, _) =>
        {
            called++;
            return Task.CompletedTask;
        });
        var request = new AgentCommandRequest
        {
            RequestId = Guid.NewGuid(),
            SessionId = snapshot.SessionId,
            Command = "sudo reboot"
        };

        var denied = await gateway.ExecuteCommandAsync(request);
        Assert.True(gateway.TryApprove(request.RequestId, out var token));

        var changed = await gateway.ExecuteCommandAsync(request with
        {
            Command = "whoami",
            ApprovalToken = token
        });

        Assert.Equal(AgentCommandStatus.Denied, changed.Status);
        Assert.False(changed.ApprovalRequired);
        Assert.Equal(0, called);
    }

    [Fact]
    public async Task UnsupportedProtocolIsRejected()
    {
        var snapshot = CreateSnapshot(SessionProtocol.RDP, isConnected: true);
        var gateway = CreateGateway(snapshot);

        var result = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "whoami"
        });

        Assert.Equal(AgentCommandStatus.UnsupportedProtocol, result.Status);
    }

    [Fact]
    public async Task CancelStopsAnInFlightDispatch()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = CreateGateway(snapshot, async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var request = new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "long-running"
        };

        var execution = gateway.ExecuteCommandAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(gateway.TryCancel(request.RequestId));
        var result = await execution;

        Assert.Equal(AgentCommandStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task TimeoutReturnsTimedOutWhenDispatchDoesNotComplete()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: true);
        var gateway = CreateGateway(snapshot, (_, cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        var result = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "long-running",
            Timeout = TimeSpan.FromMilliseconds(100)
        });

        Assert.Equal(AgentCommandStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task DisconnectedSessionIsRejected()
    {
        var snapshot = CreateSnapshot(SessionProtocol.SSH, isConnected: false);
        var gateway = CreateGateway(snapshot);

        var result = await gateway.ExecuteCommandAsync(new AgentCommandRequest
        {
            SessionId = snapshot.SessionId,
            Command = "whoami"
        });

        Assert.Equal(AgentCommandStatus.SessionNotConnected, result.Status);
    }

    private static AgentSessionGateway CreateGateway(
        params AgentSessionSnapshot[] snapshots)
    {
        return CreateGateway(snapshots[0], null, snapshots.Skip(1).ToArray());
    }

    private static AgentSessionGateway CreateGateway(
        AgentSessionSnapshot first,
        Func<AgentCommandRequest, CancellationToken, Task>? send,
        params AgentSessionSnapshot[] rest)
    {
        var endpoints = new List<IAgentSessionEndpoint>
        {
            CreateEndpoint(first, send)
        };
        endpoints.AddRange(rest.Select(snapshot => CreateEndpoint(snapshot, null)));

        return new AgentSessionGateway(new DelegateAgentSessionHost(() => endpoints));
    }

    private static IAgentSessionEndpoint CreateEndpoint(
        AgentSessionSnapshot snapshot,
        Func<AgentCommandRequest, CancellationToken, Task>? send)
    {
        return new AgentSessionEndpoint(
            () => snapshot,
            send ?? ((_, _) => Task.CompletedTask));
    }

    private static AgentSessionSnapshot CreateSnapshot(SessionProtocol protocol, bool isConnected)
    {
        var session = new SessionInfo
        {
            Name = "Test session",
            Host = "test.example",
            Username = "operator",
            Password = "password",
            Protocol = protocol
        };
        return AgentSessionSnapshot.FromSession(session, isConnected);
    }
}
