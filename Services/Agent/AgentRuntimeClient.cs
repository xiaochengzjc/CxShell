using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

/// <summary>
/// Transport-neutral typed facade for CxShell's Agent Runtime JSON contract.
/// It keeps the Runtime boundary independently testable and replaceable.
/// </summary>
public interface IAgentRuntimeClient
{
    Task<AgentRuntimeInitializeResult> InitializeAsync(
        string? protocol = null,
        string? protocolVersion = null,
        string? requestId = null,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeInfoResult> GetRuntimeInfoAsync(
        string? requestId = null,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeCapabilityResult> CheckCapabilityAsync(
        string capability,
        string? requestId = null,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeResponse> SendAsync(
        string method,
        object? parameters = null,
        string? requestId = null,
        CancellationToken cancellationToken = default);

    Task<T> SendResultAsync<T>(
        string method,
        object? parameters = null,
        string? requestId = null,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeRequestCancelResult> CancelRequestAsync(
        string requestId,
        string? cancellationRequestId = null,
        CancellationToken cancellationToken = default);

    IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer);
}

public sealed class AgentRuntimeClient : IAgentRuntimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAgentRuntimeTransport _transport;
    private long _generatedRequestId;

    public AgentRuntimeClient(IAgentRuntimeTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public Task<AgentRuntimeInitializeResult> InitializeAsync(
        string? protocol = null,
        string? protocolVersion = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        object? parameters = protocol == null && protocolVersion == null
            ? null
            : new { protocol, protocolVersion };

        return SendResultAsync<AgentRuntimeInitializeResult>(
            AgentRuntimeMethodNames.Initialize,
            parameters,
            requestId,
            cancellationToken);
    }

    public Task<AgentRuntimeInfoResult> GetRuntimeInfoAsync(
        string? requestId = null,
        CancellationToken cancellationToken = default)
        => SendResultAsync<AgentRuntimeInfoResult>(
            AgentRuntimeMethodNames.RuntimeInfo,
            requestId: requestId,
            cancellationToken: cancellationToken);

    public Task<AgentRuntimeCapabilityResult> CheckCapabilityAsync(
        string capability,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(capability))
            throw new ArgumentException("A capability is required.", nameof(capability));

        return SendResultAsync<AgentRuntimeCapabilityResult>(
            AgentRuntimeMethodNames.CapabilitiesCheck,
            new { capability },
            requestId,
            cancellationToken);
    }

    public async Task<AgentRuntimeResponse> SendAsync(
        string method,
        object? parameters = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMethod = NormalizeMethod(method);
        var normalizedRequestId = NormalizeRequestId(requestId);
        var request = new RuntimeRequestEnvelope(
            normalizedRequestId,
            normalizedMethod,
            parameters == null
                ? JsonSerializer.SerializeToElement(new { }, JsonOptions)
                : JsonSerializer.SerializeToElement(parameters, JsonOptions));
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        string responseJson;
        try
        {
            responseJson = await _transport.SendAsync(requestJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_transport is IAgentRuntimeRequestCancellationTransport cancellationTransport)
                _ = RequestRemoteCancellationAsync(cancellationTransport, normalizedRequestId);
            throw;
        }

        AgentRuntimeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AgentRuntimeResponse>(responseJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new AgentRuntimeProtocolException(
                "Runtime transport returned invalid JSON.",
                exception);
        }

        if (response == null || string.IsNullOrWhiteSpace(response.RequestId))
            throw new AgentRuntimeProtocolException("Runtime transport returned an invalid response.");
        if (!string.Equals(response.RequestId, normalizedRequestId, StringComparison.Ordinal))
        {
            throw new AgentRuntimeProtocolException(
                $"Runtime response ID '{response.RequestId}' did not match request '{normalizedRequestId}'.");
        }

        return response;
    }

    public async Task<T> SendResultAsync<T>(
        string method,
        object? parameters = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(method, parameters, requestId, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new AgentRuntimeRequestException(response);
        }

        if (response.Result is not { } result)
            throw new AgentRuntimeProtocolException(
                $"Runtime response '{response.RequestId}' did not contain a result.");

        try
        {
            return result.Deserialize<T>(JsonOptions)
                ?? throw new AgentRuntimeProtocolException(
                    $"Runtime response '{response.RequestId}' contained a null result.");
        }
        catch (JsonException exception)
        {
            throw new AgentRuntimeProtocolException(
                $"Runtime response '{response.RequestId}' contained an invalid result.",
                exception);
        }
    }

    public Task<AgentRuntimeRequestCancelResult> CancelRequestAsync(
        string requestId,
        string? cancellationRequestId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRequestId = requestId?.Trim() ?? string.Empty;
        if (normalizedRequestId.Length == 0)
            throw new ArgumentException("A target request ID is required.", nameof(requestId));

        return SendResultAsync<AgentRuntimeRequestCancelResult>(
            AgentRuntimeMethodNames.RequestCancel,
            new { requestId = normalizedRequestId },
            cancellationRequestId,
            cancellationToken);
    }

    public IDisposable SubscribeEvents(Action<AgentRuntimeEventEnvelope> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (_transport is not IAgentRuntimeEventTransport eventTransport)
        {
            throw new NotSupportedException(
                "The configured Runtime transport does not provide event notifications.");
        }

        return eventTransport.SubscribeEvents(observer);
    }

    private string NormalizeRequestId(string? requestId)
    {
        var normalized = requestId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            var next = Interlocked.Increment(ref _generatedRequestId);
            normalized = $"cxshell-runtime-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{next}";
        }

        if (normalized.Length > AgentRuntimeContract.MaximumRequestIdCharacters)
        {
            throw new ArgumentException(
                $"Request ID cannot exceed {AgentRuntimeContract.MaximumRequestIdCharacters} characters.",
                nameof(requestId));
        }

        return normalized;
    }

    private static string NormalizeMethod(string method)
    {
        var normalized = method?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("Runtime method is required.", nameof(method));
        if (normalized.Length > AgentRuntimeContract.MaximumMethodCharacters)
        {
            throw new ArgumentException(
                $"Runtime method cannot exceed {AgentRuntimeContract.MaximumMethodCharacters} characters.",
                nameof(method));
        }

        return normalized;
    }

    private static async Task RequestRemoteCancellationAsync(
        IAgentRuntimeRequestCancellationTransport transport,
        string requestId)
    {
        try
        {
            await transport.RequestCancellationAsync(requestId).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation is best-effort when the underlying stream is already closing.
        }
    }

    private sealed record RuntimeRequestEnvelope(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] JsonElement Parameters);
}

public class AgentRuntimeProtocolException : Exception
{
    public AgentRuntimeProtocolException(string message)
        : base(message)
    {
    }

    public AgentRuntimeProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AgentRuntimeRequestException : Exception
{
    public AgentRuntimeRequestException(AgentRuntimeResponse response)
        : base(response.Error ?? "Runtime request failed.")
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }

    public AgentRuntimeResponse Response { get; }
}
