using System.Text.Json;
using System.Text.Json.Serialization;
using CxShell.Models;

namespace CxShell.Services.Agent;

/// <summary>
/// CxShell's in-process Agent Runtime adapter. It exposes only the CxShell
/// session gateway and deliberately does not expose UI or connection instances.
/// </summary>
public sealed class AgentRuntimeSessionAdapter :
    IAgentRuntimeSessionAdapter,
    IAgentRuntimeModule,
    IAgentRuntimeEventSource,
    IDisposable
{
    public const string ModuleName = "session-gateway";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAgentSessionGateway _gateway;
    private readonly Func<AgentProviderSettings?> _providerSettings;
    private readonly IAgentModelClient _modelClient;
    private readonly IAgentRunCoordinator _runCoordinator;
    private readonly bool _ownsRunCoordinator;

    public AgentRuntimeSessionAdapter(
        IAgentSessionGateway gateway,
        Func<AgentProviderSettings?>? providerSettings = null,
        IAgentModelClient? modelClient = null,
        IAgentRunCoordinator? runCoordinator = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _providerSettings = providerSettings ?? (() => null);
        _modelClient = modelClient ?? new OpenAiCompatibleAgentModelClient();
        _runCoordinator = runCoordinator ?? new AgentRunCoordinator(_gateway, _providerSettings, _modelClient);
        _ownsRunCoordinator = runCoordinator == null;
    }

    public string Name => ModuleName;

    public IReadOnlyCollection<string> Methods => AgentRuntimeContract.Methods
        .Where(method => !string.Equals(method, AgentRuntimeMethodNames.RequestCancel, StringComparison.Ordinal))
        .ToArray();

    public IDisposable SubscribeRuntimeEvents(Action<AgentRuntimeModuleEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _runCoordinator.Subscribe(envelope => observer(
            new AgentRuntimeModuleEvent(
                ModuleName,
                envelope.RunId,
                AgentRuntimeMethodNames.Run,
                "run",
                envelope)));
    }

    async Task<AgentRuntimeResponse> IAgentRuntimeModule.DispatchAsync(
        AgentRuntimeRequest request,
        AgentRuntimeModuleContext context)
        => await DispatchAsync(
            request.RequestId,
            request.Method,
            request.Parameters,
            context.CancellationToken).ConfigureAwait(false);

    public async Task<AgentRuntimeResponse> DispatchAsync(
        string requestId,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequestId = requestId?.Trim() ?? string.Empty;
        var normalizedMethod = method?.Trim() ?? string.Empty;

        if (normalizedRequestId.Length == 0)
            return Error(string.Empty, AgentRuntimeErrorCodes.InvalidRequest, "Request ID is required.");
        if (normalizedRequestId.Length > AgentRuntimeContract.MaximumRequestIdCharacters)
            return Error(
                string.Empty,
                AgentRuntimeErrorCodes.InvalidRequest,
                $"Request ID cannot exceed {AgentRuntimeContract.MaximumRequestIdCharacters} characters.");

        if (normalizedMethod.Length == 0)
            return Error(normalizedRequestId, AgentRuntimeErrorCodes.InvalidRequest, "Method is required.");
        if (normalizedMethod.Length > AgentRuntimeContract.MaximumMethodCharacters)
            return Error(
                normalizedRequestId,
                AgentRuntimeErrorCodes.InvalidRequest,
                $"Method cannot exceed {AgentRuntimeContract.MaximumMethodCharacters} characters.");

        try
        {
            return normalizedMethod switch
            {
                AgentRuntimeMethodNames.Initialize => Initialize(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.Ping => Success(
                    normalizedRequestId,
                    new AgentRuntimePingResult(true, Environment.ProcessId)),
                AgentRuntimeMethodNames.RuntimeInfo => RuntimeInfo(normalizedRequestId),
                AgentRuntimeMethodNames.CapabilitiesCheck => CheckCapability(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.ProviderStatus => ProviderStatus(normalizedRequestId),
                AgentRuntimeMethodNames.ProviderTest => await TestProviderAsync(
                    normalizedRequestId,
                    cancellationToken).ConfigureAwait(false),
                AgentRuntimeMethodNames.ToolCatalog => ToolCatalog(normalizedRequestId),
                AgentRuntimeMethodNames.SessionList => ListSessions(normalizedRequestId),
                AgentRuntimeMethodNames.SessionGet => GetSession(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.AuditList => ListAudit(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunList => ListRuns(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunStatus => GetRunStatus(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunEvents => ListRunEvents(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunClear => ClearRuns(normalizedRequestId),
                AgentRuntimeMethodNames.SessionCommand => await ExecuteCommandAsync(
                    normalizedRequestId,
                    parameters,
                    cancellationToken).ConfigureAwait(false),
                AgentRuntimeMethodNames.FleetDiagnostic => await FleetDiagnosticAsync(
                    normalizedRequestId,
                    parameters,
                    cancellationToken).ConfigureAwait(false),
                AgentRuntimeMethodNames.ModelRequest => await ModelRequestAsync(
                    normalizedRequestId,
                    parameters,
                    cancellationToken).ConfigureAwait(false),
                AgentRuntimeMethodNames.Run => StartRun(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.Cancel => CancelRun(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunAppend => AppendRunMessages(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunStop => StopRun(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunResume => ResumeRun(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.SessionCommandCancel => CancelCommand(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.SessionCommandApprove => ApproveCommand(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.SessionCommandDeny => DenyCommand(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunApprove => ApproveRun(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunDeny => DenyRun(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunCredential => ProvideCredential(normalizedRequestId, parameters),
                AgentRuntimeMethodNames.RunCredentialDeny => DenyCredential(normalizedRequestId, parameters),
                _ => Error(
                    normalizedRequestId,
                    AgentRuntimeErrorCodes.UnsupportedMethod,
                    $"Unsupported method: {normalizedMethod}")
            };
        }
        catch (OperationCanceledException)
        {
            return Error(normalizedRequestId, AgentRuntimeErrorCodes.Cancelled, "Runtime request was cancelled.");
        }
        catch (AgentProviderException ex)
        {
            return Error(normalizedRequestId, AgentRuntimeErrorCodes.ProviderError, ex.SafeMessage);
        }
        catch (Exception ex)
        {
            return Error(normalizedRequestId, AgentRuntimeErrorCodes.Internal, TrimException(ex));
        }
    }

    private AgentRuntimeResponse CheckCapability(string requestId, JsonElement parameters)
    {
        var capability = GetString(parameters, "capability");
        if (string.IsNullOrWhiteSpace(capability))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "Capability is required.");

        var normalizedCapability = capability.Trim().ToLowerInvariant();
        var supported = IsCapabilitySupported(normalizedCapability);
        var reason = supported
            ? null
            : "The requested Agent capability is not available in this runtime.";

        return Success(
            requestId,
            new AgentRuntimeCapabilityResult(supported, normalizedCapability, reason));
    }

    private AgentRuntimeResponse Initialize(string requestId, JsonElement parameters)
    {
        if (!TryGetOptionalString(parameters, "protocol", out var protocol, out var protocolError))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, protocolError!);
        if (!TryGetOptionalString(parameters, "protocolVersion", out var protocolVersion, out protocolError))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, protocolError!);

        if (protocol != null && !string.Equals(
                protocol,
                AgentRuntimeContract.Protocol,
                StringComparison.Ordinal))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.ProtocolMismatch,
                $"Unsupported Agent Runtime protocol '{protocol}'. Expected '{AgentRuntimeContract.Protocol}'.");
        }

        if (protocolVersion != null && !string.Equals(
                protocolVersion,
                AgentRuntimeContract.ProtocolVersion,
                StringComparison.Ordinal))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.ProtocolMismatch,
                $"Unsupported Agent Runtime protocol version '{protocolVersion}'. Expected '{AgentRuntimeContract.ProtocolVersion}'.");
        }

        return Success(
            requestId,
            new AgentRuntimeInitializeResult(
                true,
                "cxshell-session-gateway",
                AgentRuntimeContract.RuntimeVersion)
            {
                Protocol = AgentRuntimeContract.Protocol,
                ProtocolVersion = AgentRuntimeContract.ProtocolVersion,
                Methods = AgentRuntimeContract.Methods,
                Capabilities = GetSupportedCapabilities()
            });
    }

    private AgentRuntimeResponse RuntimeInfo(string requestId)
        => Success(
            requestId,
            new AgentRuntimeInfoResult(
                "cxshell-session-gateway",
                AgentRuntimeContract.Protocol,
                AgentRuntimeContract.ProtocolVersion,
                AgentRuntimeContract.RuntimeVersion,
                AgentRuntimeContract.Methods,
                GetSupportedCapabilities(),
                _gateway.Capabilities.SupportedProtocols));

    private IReadOnlyList<string> GetSupportedCapabilities()
        => AgentRuntimeContract.Capabilities
            .Where(IsCapabilitySupported)
            .ToArray();

    private bool IsCapabilitySupported(string capability)
        => capability switch
        {
            "agent.session.list" => _gateway.Capabilities.SupportsSessionDiscovery,
            "agent.session.get" => _gateway.Capabilities.SupportsSessionDiscovery,
            "agent.session.command" => _gateway.Capabilities.SupportsTerminalCommandDispatch,
            "agent.session.command.execute" => _gateway.Capabilities.SupportsTerminalCommandDispatch &&
                                                _gateway.Capabilities.AllowsCommandExecution,
            "agent.session.command.output" => _gateway.Capabilities.SupportsCommandOutputCapture,
            "agent.diagnostics" => _gateway.Capabilities.SupportsReadOnlyDiagnostics,
            "agent.diagnostic.run" => _gateway.Capabilities.SupportsReadOnlyDiagnostics &&
                                       _gateway.Capabilities.AllowsCommandExecution,
            "agent.diagnostic.runbook" => _gateway.Capabilities.SupportsReadOnlyDiagnostics &&
                                           _gateway.Capabilities.AllowsCommandExecution,
            "agent.fleet.diagnostic" => _gateway.Capabilities.SupportsReadOnlyDiagnostics &&
                                          _gateway.Capabilities.AllowsCommandExecution,
            "agent.audit.read" => true,
            "agent.run.list" => true,
            "agent.run.status" => true,
            "agent.run.events" => true,
            "agent.session.cancel" => _gateway.Capabilities.SupportsTerminalCommandDispatch,
            "agent.session-command.cancel" => _gateway.Capabilities.SupportsTerminalCommandDispatch,
            "agent.session.command.approval" => _gateway.Capabilities.RequiresApprovalForDangerousCommands ||
                                                 _gateway.Capabilities.RequiresApprovalForChangeCommands,
            "agent.session.command.change-approval" => _gateway.Capabilities.RequiresApprovalForChangeCommands,
            "runtime.request.cancel" => true,
             "agent.provider.status" => true,
             "agent.provider.test" => true,
            "agent.provider.tools" => GetProviderCapabilities().SupportsTools,
            "agent.provider.streaming" => GetProviderCapabilities().SupportsStreaming,
            "agent.provider.vision" => GetProviderCapabilities().SupportsVision,
            "agent.provider.documents" => GetProviderCapabilities().SupportsDocumentInput,
            "agent.provider.responses" => GetProviderCapabilities().SupportsResponsesApi,
            "agent.provider.usage" => GetProviderCapabilities().SupportsTokenUsage,
            "agent.provider.reasoning" => GetProviderCapabilities().SupportsReasoning,
            "agent.model.request" => true,
            "agent.tool.catalog" => true,
            "agent.run" => true,
            "agent.run.append" => true,
            "agent.run.stop" => true,
            "agent.run.resume" => true,
            "agent.run.approval" => true,
            _ => false
        };

    private AgentProviderCapabilities GetProviderCapabilities()
        => AgentProviderConfiguration.GetCapabilities(_providerSettings());

    private AgentRuntimeResponse ListSessions(string requestId)
        => Success(
            requestId,
            new AgentRuntimeSessionListResult(_gateway.GetSessions()));

    private AgentRuntimeResponse ListAudit(string requestId, JsonElement parameters)
    {
        var limit = GetInt(parameters, "limit", 50);
        limit = Math.Clamp(limit, 1, AgentAuditLog.MaximumEntries);
        return Success(
            requestId,
            new AgentRuntimeAuditListResult(_gateway.ReadAudit(limit), limit));
    }

    private AgentRuntimeResponse ListRuns(string requestId, JsonElement parameters)
    {
        var limit = Math.Clamp(
            GetInt(parameters, "limit", AgentRunCoordinator.DefaultRunListLimit),
            1,
            AgentRunCoordinator.MaximumRetainedRuns);
        return Success(
            requestId,
            new AgentRuntimeRunListResult(_runCoordinator.GetRecentRuns(limit), limit));
    }

    private AgentRuntimeResponse GetRunStatus(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A runId is required.");

        var run = _runCoordinator.GetRun(runId);
        return Success(
            requestId,
            new AgentRuntimeRunStatusResult(
                run != null,
                run,
                run == null ? "The agent run was not found or its history has expired." : null));
    }

    private AgentRuntimeResponse ClearRuns(string requestId)
        => Success(
            requestId,
            new AgentRuntimeRunClearResult(_runCoordinator.ClearCompletedRuns()));

    private AgentRuntimeResponse ListRunEvents(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A runId is required.");

        var afterSequence = GetLong(parameters, "afterSequence", 0);
        if (afterSequence < 0)
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "afterSequence cannot be negative.");

        var limit = Math.Clamp(
            GetInt(parameters, "limit", AgentRunCoordinator.DefaultEventReadLimit),
            1,
            AgentRunCoordinator.MaximumEventReadLimit);
        var result = _runCoordinator.ReadEvents(runId, afterSequence, limit);
        return result == null
            ? Error(
                requestId,
                AgentRuntimeErrorCodes.RunNotFound,
                "The agent run was not found or its event history has expired.")
            : Success(requestId, result);
    }

    private async Task<AgentRuntimeResponse> FleetDiagnosticAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var scope = GetString(parameters, "scope");
        if (string.IsNullOrWhiteSpace(scope))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A diagnostic scope is required.");

        var result = await _gateway.RunReadOnlyDiagnosticAcrossSessionsAsync(
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        return Success(requestId, result);
    }

    private AgentRuntimeResponse ProviderStatus(string requestId)
    {
        var settings = _providerSettings();
        var validation = AgentProviderConfiguration.Validate(settings);
        return Success(
            requestId,
            new AgentRuntimeProviderStatusResult(
                validation.IsValid,
                AgentProviderConfiguration.ToSnapshot(settings),
                validation.Status,
                validation.Message));
    }

    private async Task<AgentRuntimeResponse> TestProviderAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var provider = _providerSettings();
        var validation = AgentProviderConfiguration.Validate(provider);
        if (!validation.IsValid || provider == null)
        {
            return Success(
                requestId,
                new AgentRuntimeProviderTestResult(
                    false,
                    provider?.BuiltinId ?? string.Empty,
                    provider?.Model ?? string.Empty,
                    0,
                    validation.Message,
                    validation.Status.ToString())
                {
                    Capabilities = AgentProviderConfiguration.GetCapabilities(provider)
                });
        }

        var startedAt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var response = await _modelClient.CompleteAsync(
                    provider,
                    new AgentModelRequest(
                        [new AgentChatMessage(
                            "user",
                            "Connectivity test. Reply with OK only.")],
                        Model: provider.Model,
                        MaxTokens: 16),
                    timeout.Token)
                .ConfigureAwait(false);
            return Success(
                requestId,
                new AgentRuntimeProviderTestResult(
                    true,
                    response.Provider,
                    response.Model,
                    ElapsedMilliseconds(startedAt),
                    "The provider responded successfully.")
                {
                    Capabilities = AgentProviderConfiguration.GetCapabilities(provider)
                });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Success(
                requestId,
                new AgentRuntimeProviderTestResult(
                    false,
                    provider.BuiltinId,
                    provider.Model,
                    ElapsedMilliseconds(startedAt),
                    "The provider connectivity test timed out.",
                    AgentProviderErrorKind.Timeout.ToString())
                {
                    Capabilities = AgentProviderConfiguration.GetCapabilities(provider)
                });
        }
        catch (AgentProviderException exception)
        {
            return Success(
                requestId,
                new AgentRuntimeProviderTestResult(
                    false,
                    provider.BuiltinId,
                    provider.Model,
                    ElapsedMilliseconds(startedAt),
                    exception.SafeMessage,
                    exception.Kind.ToString())
                {
                    Capabilities = AgentProviderConfiguration.GetCapabilities(provider)
                });
        }
        catch (Exception exception)
        {
            return Success(
                requestId,
                new AgentRuntimeProviderTestResult(
                    false,
                    provider.BuiltinId,
                    provider.Model,
                    ElapsedMilliseconds(startedAt),
                    TrimException(exception),
                    exception.GetType().Name)
                {
                    Capabilities = AgentProviderConfiguration.GetCapabilities(provider)
                });
        }
    }

    private static long ElapsedMilliseconds(DateTimeOffset startedAt)
        => Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

    private AgentRuntimeResponse ToolCatalog(string requestId)
    {
        var capabilities = _gateway.Capabilities;
        var tools = AgentRunCoordinator.GetToolDefinitions()
            .Select(tool =>
            {
                var availability = GetToolAvailability(tool.Name, capabilities);
                return new AgentRuntimeToolDescriptor(
                    tool.Name,
                    tool.Description,
                    tool.Parameters,
                    availability.Available,
                    availability.Reason);
            })
            .ToArray();

        return Success(
            requestId,
            new AgentRuntimeToolCatalogResult(
                tools,
                capabilities.RequiresApprovalForDangerousCommands,
                capabilities.RequiresApprovalForChangeCommands));
    }

    private static (bool Available, string? Reason) GetToolAvailability(
        string toolName,
        AgentGatewayCapabilities capabilities)
    {
        if (string.Equals(toolName, AgentRunCoordinator.SessionInfoToolName, StringComparison.Ordinal))
        {
            return capabilities.SupportsSessionDiscovery
                ? (true, null)
                : (false, "Session discovery is not available in the current gateway.");
        }

        if (string.Equals(toolName, AgentRunCoordinator.SessionCommandToolName, StringComparison.Ordinal))
        {
            return capabilities.SupportsTerminalCommandDispatch && capabilities.AllowsCommandExecution
                ? (true, null)
                : (false, "Terminal command execution is disabled in the current gateway.");
        }

        return capabilities.SupportsReadOnlyDiagnostics && capabilities.AllowsCommandExecution
            ? (true, null)
            : (false, "Read-only diagnostic execution is not available in the current gateway.");
    }

    private AgentRuntimeResponse GetSession(string requestId, JsonElement parameters)
    {
        if (!TryGetGuid(parameters, "sessionId", out var sessionId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A valid sessionId is required.");

        var session = _gateway.GetSession(sessionId);
        return Success(
            requestId,
            new AgentRuntimeSessionGetResult(
                session != null,
                session,
                session == null ? "Session was not found or is not an SSH session." : null));
    }

    private async Task<AgentRuntimeResponse> ExecuteCommandAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!TryGetGuid(parameters, "sessionId", out var sessionId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A valid sessionId is required.");

        var command = GetString(parameters, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A non-empty command is required.");
        if (command.Length > AgentSessionGateway.MaximumCommandLength)
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                $"Command length cannot exceed {AgentSessionGateway.MaximumCommandLength} characters.");

        var hasExplicitTimeout = parameters.ValueKind == JsonValueKind.Object &&
                                 parameters.TryGetProperty("timeoutMs", out _);
        var timeoutMs = GetInt(
            parameters,
            "timeoutMs",
            (int)AgentSessionGateway.DefaultCommandTimeout.TotalMilliseconds);
        if (timeoutMs < 100 || timeoutMs > (int)AgentSessionGateway.MaximumCommandTimeout.TotalMilliseconds)
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                $"timeoutMs must be between 100 and {(int)AgentSessionGateway.MaximumCommandTimeout.TotalMilliseconds}.");
        }

        var timeout = AgentCommandTimeoutPolicy.Resolve(
            command,
            TimeSpan.FromMilliseconds(timeoutMs),
            hasExplicitTimeout);
        var request = new AgentCommandRequest
        {
            RequestId = TryGetGuid(parameters, "requestId", out var requestIdValue)
                ? requestIdValue
                : Guid.NewGuid(),
            SessionId = sessionId,
            Command = command,
            Timeout = timeout,
            AppendLineEnding = GetBool(parameters, "appendLineEnding", true),
            ApprovalToken = GetString(parameters, "approvalToken")
        };

        var result = await _gateway.ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(false);
        return Success(requestId, new AgentRuntimeSessionCommandResult(result));
    }

    private async Task<AgentRuntimeResponse> ModelRequestAsync(
        string requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var provider = _providerSettings();
        var validation = AgentProviderConfiguration.Validate(provider);
        if (!validation.IsValid || provider == null)
            return Error(requestId, AgentRuntimeErrorCodes.ProviderUnavailable, validation.Message);

        if (!TryReadMessages(parameters, out var messages, out var messageError))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, messageError!);

        var modelRequest = new AgentModelRequest(
            messages,
            GetString(parameters, "model"),
            GetDouble(parameters, "temperature"),
            GetIntNullable(parameters, "maxTokens"));
        var response = await _modelClient.CompleteAsync(
            provider,
            modelRequest,
            cancellationToken).ConfigureAwait(false);
        return Success(requestId, new AgentRuntimeModelRequestResult(response));
    }

    private AgentRuntimeResponse StartRun(string requestId, JsonElement parameters)
    {
        if (!TryGetGuid(parameters, "sessionId", out var sessionId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A valid sessionId is required.");

        if (!TryReadMessages(parameters, out var messages, out var messageError))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, messageError!);

        var timeoutMs = GetInt(
            parameters,
            "timeoutMs",
            (int)AgentRunCoordinator.DefaultRunTimeout.TotalMilliseconds);
        var start = _runCoordinator.Start(new AgentRunRequest
        {
            RunId = GetString(parameters, "runId"),
            SessionId = sessionId,
            Messages = messages,
            Model = GetString(parameters, "model"),
            Temperature = GetDouble(parameters, "temperature"),
            MaxTokens = GetIntNullable(parameters, "maxTokens"),
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        });
        if (!start.Started)
            return Error(
                requestId,
                AgentRuntimeErrorCodes.RunRejected,
                start.Error ?? "Agent run could not be started.");

        return Success(
            requestId,
            new AgentRuntimeRunResult(
                true,
                start.RunId,
                sessionId.ToString("D")));
    }

    private AgentRuntimeResponse CancelRun(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A runId is required.");

        var result = _runCoordinator.Cancel(runId);
        return Success(
            requestId,
            new AgentRuntimeCancelResult(result.Cancelled, result.RunId, result.Error));
    }

    private AgentRuntimeResponse AppendRunMessages(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A runId is required.");

        if (!TryReadFollowUpMessages(parameters, out var messages, out var messageError))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                messageError!);
        }

        var result = _runCoordinator.AppendMessages(runId, messages);
        return Success(
            requestId,
            new AgentRuntimeRunAppendResult(
                result.Appended,
                result.RunId,
                result.MessageCount,
                result.Error));
    }

    private AgentRuntimeResponse StopRun(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A runId is required.");

        var result = _runCoordinator.RequestStop(runId);
        return Success(
            requestId,
            new AgentRuntimeRunStopResult(result.Requested, result.RunId, result.Error));
    }

    private AgentRuntimeResponse ResumeRun(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A runId is required.");

        var result = _runCoordinator.Resume(runId);
        return Success(
            requestId,
            new AgentRuntimeRunResumeResult(
                result.Resumed,
                result.PreviousRunId,
                result.RunId,
                result.SessionId.ToString("D"),
                result.Error));
    }

    private AgentRuntimeResponse ApproveRun(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        var toolCallId = GetString(parameters, "toolCallId");
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(toolCallId))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                "runId and toolCallId are required.");
        }

        var result = _runCoordinator.Approve(runId, toolCallId);
        return Success(
            requestId,
            new AgentRuntimeRunApprovalResult(
                result.Decided,
                result.Approved,
                result.RunId,
                result.ToolCallId,
                result.Error));
    }

    private AgentRuntimeResponse DenyRun(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        var toolCallId = GetString(parameters, "toolCallId");
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(toolCallId))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                "runId and toolCallId are required.");
        }

        var result = _runCoordinator.Deny(runId, toolCallId);
        return Success(
            requestId,
            new AgentRuntimeRunApprovalResult(
                result.Decided,
                result.Approved,
                result.RunId,
                result.ToolCallId,
                result.Error));
    }

    private AgentRuntimeResponse ProvideCredential(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        var credentialRequestId = GetString(parameters, "credentialRequestId");
        var value = GetString(parameters, "value");
        if (string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(credentialRequestId) ||
            string.IsNullOrEmpty(value))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                "runId, credentialRequestId and a non-empty value are required.");
        }

        var result = _runCoordinator.ProvideCredential(
            runId,
            credentialRequestId,
            value,
            GetBool(parameters, "rememberForRun", false));
        return Success(
            requestId,
            new AgentRuntimeRunCredentialResult(
                result.Provided,
                result.RunId,
                result.CredentialRequestId,
                result.Error));
    }

    private AgentRuntimeResponse DenyCredential(string requestId, JsonElement parameters)
    {
        var runId = GetString(parameters, "runId");
        var credentialRequestId = GetString(parameters, "credentialRequestId");
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(credentialRequestId))
        {
            return Error(
                requestId,
                AgentRuntimeErrorCodes.InvalidParameters,
                "runId and credentialRequestId are required.");
        }

        var result = _runCoordinator.DenyCredential(runId, credentialRequestId);
        return Success(
            requestId,
            new AgentRuntimeRunCredentialResult(
                result.Provided,
                result.RunId,
                result.CredentialRequestId,
                result.Error));
    }

    private AgentRuntimeResponse CancelCommand(string requestId, JsonElement parameters)
    {
        if (!TryGetGuid(parameters, "requestId", out var commandRequestId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A valid requestId is required.");

        return Success(
            requestId,
            new AgentRuntimeSessionCommandCancelResult(
                _gateway.TryCancel(commandRequestId),
                commandRequestId.ToString("D")));
    }

    private AgentRuntimeResponse ApproveCommand(string requestId, JsonElement parameters)
    {
        if (!TryGetGuid(parameters, "requestId", out var commandRequestId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A valid requestId is required.");

        var approved = _gateway.TryApprove(commandRequestId, out var approvalToken);
        return Success(
            requestId,
            new AgentRuntimeSessionCommandApprovalResult(
                approved,
                commandRequestId.ToString("D"),
                approved ? approvalToken : null,
                approved ? null : "The approval request was not found or has expired."));
    }

    private AgentRuntimeResponse DenyCommand(string requestId, JsonElement parameters)
    {
        if (!TryGetGuid(parameters, "requestId", out var commandRequestId))
            return Error(requestId, AgentRuntimeErrorCodes.InvalidParameters, "A valid requestId is required.");

        var denied = _gateway.TryDeny(commandRequestId);
        return Success(
            requestId,
            new AgentRuntimeSessionCommandApprovalResult(
                false,
                commandRequestId.ToString("D"),
                Error: denied ? "The command approval was denied." : "The approval request was not found or has expired."));
    }

    public void Dispose()
    {
        if (_ownsRunCoordinator && _runCoordinator is IDisposable disposable)
            disposable.Dispose();
    }

    private static AgentRuntimeResponse Success(string requestId, object result)
        => new()
        {
            RequestId = requestId,
            Ok = true,
            Result = JsonSerializer.SerializeToElement(result, JsonOptions)
        };

    private static AgentRuntimeResponse Error(string requestId, string errorCode, string error)
        => new()
        {
            RequestId = requestId,
            Ok = false,
            ErrorCode = errorCode,
            Error = error
        };

    private static string? GetString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool TryGetOptionalString(
        JsonElement parameters,
        string name,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error = "initialize params must be a JSON object.";
            return false;
        }
        if (!parameters.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"initialize parameter '{name}' must be a non-empty string.";
            return false;
        }

        value = property.GetString()!.Trim();
        return true;
    }

    private static bool TryGetGuid(JsonElement parameters, string name, out Guid value)
        => Guid.TryParse(GetString(parameters, name), out value) && value != Guid.Empty;

    private static int GetInt(JsonElement parameters, string name, int defaultValue)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(name, out var value) &&
            value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static int? GetIntNullable(JsonElement parameters, string name)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(name, out var value) &&
            value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long GetLong(JsonElement parameters, string name, long defaultValue)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(name, out var value) &&
            value.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static double? GetDouble(JsonElement parameters, string name)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(name, out var value) &&
            value.TryGetDouble(out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool GetBool(JsonElement parameters, string name, bool defaultValue)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(name, out var value) &&
            (value.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return value.GetBoolean();
        }

        return defaultValue;
    }

    private static bool TryReadMessages(
        JsonElement parameters,
        out IReadOnlyList<AgentChatMessage> messages,
        out string? error)
    {
        messages = [];
        error = null;
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("messages", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            error = "A messages array is required.";
            return false;
        }

        if (value.GetArrayLength() > AgentRuntimeContract.MaximumMessageCount)
        {
            error = $"The messages array cannot contain more than {AgentRuntimeContract.MaximumMessageCount} items.";
            return false;
        }

        var parsed = new List<AgentChatMessage>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("role", out var role) ||
                role.ValueKind != JsonValueKind.String)
            {
                error = "Each message requires a string role field.";
                return false;
            }

            var roleText = role.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            if (roleText is not ("system" or "user" or "assistant"))
            {
                error = "Message role must be system, user or assistant.";
                return false;
            }
            if (!TryReadMessageContent(item, out var contentText, out var contentParts, out error))
                return false;

            if (string.IsNullOrWhiteSpace(contentText) && contentParts.Count == 0)
            {
                error = "Each message requires text content or content parts.";
                return false;
            }

            if (contentText.Length > AgentRuntimeContract.MaximumMessageCharacters)
            {
                error = $"Each message cannot exceed {AgentRuntimeContract.MaximumMessageCharacters} characters.";
                return false;
            }

            parsed.Add(new AgentChatMessage(roleText, contentText, ContentParts: contentParts));
        }

        messages = parsed;
        return true;
    }

    private static bool TryReadMessageContent(
        JsonElement item,
        out string contentText,
        out IReadOnlyList<AgentContentPart> contentParts,
        out string? error)
    {
        contentText = string.Empty;
        contentParts = [];
        error = null;

        if (item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                contentText = content.GetString() ?? string.Empty;
            }
            else if (content.ValueKind != JsonValueKind.Null)
            {
                error = "Message content must be a string or null.";
                return false;
            }
        }

        if (!item.TryGetProperty("contentParts", out var partsElement))
            return true;
        if (partsElement.ValueKind == JsonValueKind.Null)
            return true;
        if (partsElement.ValueKind != JsonValueKind.Array)
        {
            error = "Message contentParts must be an array.";
            return false;
        }

        var parsed = new List<AgentContentPart>();
        foreach (var part in partsElement.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                error = "Each content part requires a string type.";
                return false;
            }

            var typeText = type.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            var text = ReadOptionalString(part, "text");
            var mediaType = ReadOptionalString(part, "mediaType");
            var data = ReadOptionalString(part, "data");
            var fileName = ReadOptionalString(part, "fileName");
            if (typeText == "image")
            {
                if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(data))
                {
                    error = "Image content parts require mediaType and data.";
                    return false;
                }
            }
            else if (typeText != "text" || string.IsNullOrWhiteSpace(text))
            {
                error = "Content parts must be non-empty text or image parts.";
                return false;
            }

            parsed.Add(new AgentContentPart(typeText, text, mediaType, data, fileName));
        }

        contentParts = parsed;
        return true;
    }

    private static string? ReadOptionalString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadFollowUpMessages(
        JsonElement parameters,
        out IReadOnlyList<AgentChatMessage> messages,
        out string? error)
    {
        if (!TryReadMessages(parameters, out messages, out error))
            return false;

        if (messages.Count > AgentRunCoordinator.MaximumAppendedMessagesPerRun)
        {
            error =
                $"A single append cannot contain more than {AgentRunCoordinator.MaximumAppendedMessagesPerRun} messages.";
            return false;
        }

        if (messages.Any(message =>
                !string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message.Content)))
        {
            error = "Follow-up messages must be non-empty user messages.";
            return false;
        }

        return true;
    }

    private static string TrimException(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500] + "...";
    }
}
