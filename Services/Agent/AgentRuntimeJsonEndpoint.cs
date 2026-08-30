using System.Text.Json;
using System.Text.Json.Serialization;

namespace CxShell.Services.Agent;

/// <summary>
/// Transport-neutral JSON boundary for the in-process Runtime Host. A future
/// named-pipe or Unix-socket transport can forward one JSON document per request
/// without changing module or session-gateway code.
/// </summary>
public sealed class AgentRuntimeJsonEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAgentRuntimeHost _host;

    public AgentRuntimeJsonEndpoint(IAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<string> DispatchAsync(
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return SerializeError(string.Empty, AgentRuntimeErrorCodes.InvalidRequest, "Request JSON is required.");
        if (requestJson.Length > AgentRuntimeContract.MaximumJsonRequestCharacters)
            return SerializeError(
                string.Empty,
                AgentRuntimeErrorCodes.InvalidRequest,
                $"Request JSON cannot exceed {AgentRuntimeContract.MaximumJsonRequestCharacters} characters.");

        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return SerializeError(
                    string.Empty,
                    AgentRuntimeErrorCodes.InvalidRequest,
                    "Request JSON must be an object.");

            var root = document.RootElement;
            var request = new AgentRuntimeRequest(
                ReadString(root, "requestId") ?? string.Empty,
                ReadString(root, "method") ?? string.Empty,
                ReadParameters(root));
            var response = await _host.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (JsonException)
        {
            return SerializeError(
                string.Empty,
                AgentRuntimeErrorCodes.InvalidRequest,
                "Request JSON is invalid.");
        }
        catch (OperationCanceledException)
        {
            return SerializeError(
                string.Empty,
                AgentRuntimeErrorCodes.Cancelled,
                "Runtime request was cancelled.");
        }
        catch (Exception exception)
        {
            return SerializeError(
                string.Empty,
                AgentRuntimeErrorCodes.Internal,
                TrimException(exception));
        }
    }

    private static JsonElement ReadParameters(JsonElement root)
    {
        if (root.TryGetProperty("params", out var parameters))
            return parameters.Clone();
        if (root.TryGetProperty("parameters", out parameters))
            return parameters.Clone();

        return default;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string SerializeError(string requestId, string errorCode, string error)
        => JsonSerializer.Serialize(
            new AgentRuntimeResponse
            {
                RequestId = requestId,
                Ok = false,
                ErrorCode = errorCode,
                Error = error
            },
            JsonOptions);

    private static string TrimException(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500] + "...";
    }
}
