using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services.Agent;

/// <summary>
/// Minimal non-streaming OpenAI Chat Completions client. It is intentionally
/// provider-neutral and covers Routin AI's OpenAI-compatible Plan endpoint.
/// </summary>
public sealed class OpenAiCompatibleAgentModelClient : IAgentModelClient, IAgentStreamingModelClient
{
    private const int MaximumMessages = 100;
    private const int MaximumMessageCharacters = 128 * 1024;
    private const int MaximumResponseCharacters = 512 * 1024;
    private const int MaximumRequestBytes = 12 * 1024 * 1024;
    private const int MaximumToolCalls = AgentRunCoordinator.MaximumToolCallsPerRun;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleAgentModelClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<AgentModelResponse> CompleteAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        ValidateRequest(provider, request);

        if (AgentProviderConfiguration.IsResponsesProvider(provider))
        {
            return await CompleteResponsesAsync(provider, request, cancellationToken)
                .ConfigureAwait(false);
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? provider.Model.Trim() : request.Model.Trim(),
            messages = request.Messages.Select(ToWireMessage),
            stream = false,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            tools = request.Tools?.Select(ToWireTool),
            tool_choice = request.Tools is { Count: > 0 } ? "auto" : null
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProviderConfiguration.BuildChatCompletionsUri(provider));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var apiKey = AgentProviderConfiguration.GetApiKey(provider);
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            SerializeRequest(body),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(provider.RequestTimeoutSeconds));
        try
        {
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var responseText = await ReadResponseTextAsync(response, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw AgentProviderException.FromStatusCode(
                    (int)response.StatusCode,
                    httpRequest.RequestUri?.ToString());

            return ParseChatResponse(responseText, provider);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw AgentProviderException.Timeout(httpRequest.RequestUri?.ToString(), exception);
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (exception.StatusCode is { } statusCode)
                throw AgentProviderException.FromStatusCode(
                    (int)statusCode,
                    httpRequest.RequestUri?.ToString());
            throw AgentProviderException.Network(exception, httpRequest.RequestUri?.ToString());
        }
    }

    public Task<AgentModelResponse> CompleteStreamingAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        Action<AgentModelStreamChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        ValidateRequest(provider, request);

        return AgentProviderConfiguration.IsResponsesProvider(provider)
            ? CompleteResponsesStreamingAsync(provider, request, onChunk, cancellationToken)
            : CompleteChatStreamingAsync(provider, request, onChunk, cancellationToken);
    }

    private async Task<AgentModelResponse> CompleteChatStreamingAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        Action<AgentModelStreamChunk> onChunk,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? provider.Model.Trim() : request.Model.Trim(),
            messages = request.Messages.Select(ToWireMessage),
            stream = true,
            stream_options = new { include_usage = true },
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            tools = request.Tools?.Select(ToWireTool),
            tool_choice = request.Tools is { Count: > 0 } ? "auto" : null
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProviderConfiguration.BuildChatCompletionsUri(provider));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var apiKey = AgentProviderConfiguration.GetApiKey(provider);
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            SerializeRequest(body),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(provider.RequestTimeoutSeconds));
        try
        {
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _ = await ReadResponseTextAsync(response, timeout.Token).ConfigureAwait(false);
                throw AgentProviderException.FromStatusCode(
                    (int)response.StatusCode,
                    httpRequest.RequestUri?.ToString());
            }

            return await ReadChatStreamAsync(
                    response,
                    provider,
                    onChunk,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw AgentProviderException.Timeout(httpRequest.RequestUri?.ToString(), exception);
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (exception.StatusCode is { } statusCode)
                throw AgentProviderException.FromStatusCode(
                    (int)statusCode,
                    httpRequest.RequestUri?.ToString());
            throw AgentProviderException.Network(exception, httpRequest.RequestUri?.ToString());
        }
        catch (AgentProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw AgentProviderException.Protocol(
                "Provider returned an invalid streaming Chat Completions response.",
                exception);
        }
    }

    private async Task<AgentModelResponse> CompleteResponsesStreamingAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        Action<AgentModelStreamChunk> onChunk,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? provider.Model.Trim() : request.Model.Trim(),
            input = request.Messages.SelectMany(ToResponsesInput).ToList(),
            stream = true,
            store = false,
            temperature = request.Temperature,
            max_output_tokens = request.MaxTokens,
            tools = request.Tools?.Select(ToResponsesTool),
            tool_choice = request.Tools is { Count: > 0 } ? "auto" : null
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProviderConfiguration.BuildResponsesUri(provider));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var apiKey = AgentProviderConfiguration.GetApiKey(provider);
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            SerializeRequest(body),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(provider.RequestTimeoutSeconds));
        try
        {
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _ = await ReadResponseTextAsync(response, timeout.Token).ConfigureAwait(false);
                throw AgentProviderException.FromStatusCode(
                    (int)response.StatusCode,
                    httpRequest.RequestUri?.ToString());
            }

            return await ReadResponsesStreamAsync(
                    response,
                    provider,
                    onChunk,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw AgentProviderException.Timeout(httpRequest.RequestUri?.ToString(), exception);
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (exception.StatusCode is { } statusCode)
                throw AgentProviderException.FromStatusCode(
                    (int)statusCode,
                    httpRequest.RequestUri?.ToString());
            throw AgentProviderException.Network(exception, httpRequest.RequestUri?.ToString());
        }
        catch (AgentProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw AgentProviderException.Protocol(
                "Provider returned an invalid streaming Responses API response.",
                exception);
        }
    }

    private static async Task<AgentModelResponse> ReadChatStreamAsync(
        HttpResponseMessage response,
        AgentProviderSettings provider,
        Action<AgentModelStreamChunk> onChunk,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024);
        var text = new StringBuilder();
        var toolBuffers = new Dictionary<int, StreamingToolCallBuffer>();
        var rawResponse = new StringBuilder();
        var dataBuilder = new StringBuilder();
        var sawSsePayload = false;
        var model = provider.Model;
        int? inputTokens = null;
        int? outputTokens = null;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (dataBuilder.Length > 0)
                {
                    if (ProcessChatStreamData(
                            dataBuilder.ToString(),
                            toolBuffers,
                            text,
                            ref model,
                            ref inputTokens,
                            ref outputTokens,
                            onChunk))
                    {
                        break;
                    }

                    dataBuilder.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                    dataBuilder.Append('\n');
                dataBuilder.Append(line[5..].TrimStart());
                sawSsePayload = true;
                continue;
            }

            if (!sawSsePayload && !line.StartsWith("event:", StringComparison.Ordinal))
            {
                if (rawResponse.Length > 0)
                    rawResponse.Append('\n');
                rawResponse.Append(line);
            }
        }

        if (dataBuilder.Length > 0)
            ProcessChatStreamData(
                dataBuilder.ToString(),
                toolBuffers,
                text,
                ref model,
                ref inputTokens,
                ref outputTokens,
                onChunk);

        if (!sawSsePayload && rawResponse.Length > 0)
        {
            var responseText = ParseChatResponse(rawResponse.ToString(), provider);
            if (!string.IsNullOrEmpty(responseText.Text))
                onChunk(new AgentModelStreamChunk(responseText.Text));
            return responseText;
        }

        var toolCalls = toolBuffers.Values
            .OrderBy(buffer => buffer.Index)
            .Where(buffer => !string.IsNullOrWhiteSpace(buffer.Id) ||
                             !string.IsNullOrWhiteSpace(buffer.Name) ||
                             buffer.Arguments.Length > 0)
            .Select(buffer => CreateValidatedToolCall(
                buffer.Id,
                buffer.Name,
                buffer.Arguments.ToString()))
            .ToArray();
        return new AgentModelResponse(
            text.ToString(),
            model,
            provider.BuiltinId,
            inputTokens,
            outputTokens,
            toolCalls.Length == 0 ? null : toolCalls);
    }

    private static bool ProcessChatStreamData(
        string data,
        Dictionary<int, StreamingToolCallBuffer> toolBuffers,
        StringBuilder text,
        ref string model,
        ref int? inputTokens,
        ref int? outputTokens,
        Action<AgentModelStreamChunk> onChunk)
    {
        if (data == "[DONE]")
            return true;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var modelText = ReadString(root, "model");
        if (!string.IsNullOrWhiteSpace(modelText))
            model = modelText;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = ReadInt(usage, "prompt_tokens") ?? inputTokens;
            outputTokens = ReadInt(usage, "completion_tokens") ?? outputTokens;
        }

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object)
        {
            var thinking = ReadString(delta, "reasoning_content");
            if (string.IsNullOrEmpty(thinking))
                thinking = ReadString(delta, "reasoning");
            if (!string.IsNullOrEmpty(thinking))
                onChunk(new AgentModelStreamChunk(Thinking: thinking));

            var content = delta.TryGetProperty("content", out var contentElement)
                ? ReadContentText(contentElement)
                : string.Empty;
            if (!string.IsNullOrEmpty(content))
            {
                text.Append(content);
                EnsureStreamingResponseSize(text.Length);
                onChunk(new AgentModelStreamChunk(content));
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var fragment in toolCalls.EnumerateArray())
                {
                    var index = fragment.TryGetProperty("index", out var indexElement) &&
                                indexElement.TryGetInt32(out var parsedIndex)
                        ? parsedIndex
                        : toolBuffers.Count;
                    if (!toolBuffers.TryGetValue(index, out var buffer))
                    {
                        if (toolBuffers.Count >= MaximumToolCalls)
                            throw new InvalidOperationException(
                                $"Provider response contained more than {MaximumToolCalls} tool calls.");
                        buffer = new StreamingToolCallBuffer(index);
                        toolBuffers[index] = buffer;
                    }

                    var id = ReadString(fragment, "id");
                    if (!string.IsNullOrWhiteSpace(id) && string.IsNullOrEmpty(buffer.Id))
                        buffer.Id = id;
                    if (fragment.TryGetProperty("function", out var function) &&
                        function.ValueKind == JsonValueKind.Object)
                    {
                        var name = ReadString(function, "name");
                        if (!string.IsNullOrWhiteSpace(name))
                            buffer.Name = name;
                        buffer.Arguments.Append(ReadString(function, "arguments"));
                    }
                }
            }
        }

        return false;
    }

    private static async Task<AgentModelResponse> ReadResponsesStreamAsync(
        HttpResponseMessage response,
        AgentProviderSettings provider,
        Action<AgentModelStreamChunk> onChunk,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024);
        var text = new StringBuilder();
        var toolBuffers = new Dictionary<string, StreamingToolCallBuffer>(StringComparer.Ordinal);
        var rawResponse = new StringBuilder();
        var dataBuilder = new StringBuilder();
        var sawSsePayload = false;
        var model = provider.Model;
        int? inputTokens = null;
        int? outputTokens = null;
        IReadOnlyList<AgentToolCall>? completedToolCalls = null;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (dataBuilder.Length > 0)
                {
                    ProcessResponsesStreamData(
                        dataBuilder.ToString(),
                        toolBuffers,
                        text,
                        ref model,
                        ref inputTokens,
                        ref outputTokens,
                        ref completedToolCalls,
                        onChunk,
                        provider);
                    dataBuilder.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuilder.Length > 0)
                    dataBuilder.Append('\n');
                dataBuilder.Append(line[5..].TrimStart());
                sawSsePayload = true;
                continue;
            }

            if (!sawSsePayload && !line.StartsWith("event:", StringComparison.Ordinal))
            {
                if (rawResponse.Length > 0)
                    rawResponse.Append('\n');
                rawResponse.Append(line);
            }
        }

        if (dataBuilder.Length > 0)
        {
            ProcessResponsesStreamData(
                dataBuilder.ToString(),
                toolBuffers,
                text,
                ref model,
                ref inputTokens,
                ref outputTokens,
                ref completedToolCalls,
                onChunk,
                provider);
        }

        if (!sawSsePayload && rawResponse.Length > 0)
        {
            var responseText = ParseResponsesResponse(rawResponse.ToString(), provider);
            if (!string.IsNullOrEmpty(responseText.Text))
                onChunk(new AgentModelStreamChunk(responseText.Text));
            return responseText;
        }

        if (completedToolCalls is { Count: > 0 })
        {
            return new AgentModelResponse(
                text.ToString(),
                model,
                provider.BuiltinId,
                inputTokens,
                outputTokens,
                completedToolCalls);
        }

        var toolCalls = toolBuffers.Values
            .OrderBy(buffer => buffer.Index)
            .Where(buffer => !string.IsNullOrWhiteSpace(buffer.Id) ||
                             !string.IsNullOrWhiteSpace(buffer.Name) ||
                             buffer.Arguments.Length > 0)
            .Select(buffer => CreateValidatedToolCall(
                buffer.Id,
                buffer.Name,
                buffer.Arguments.ToString()))
            .ToArray();
        return new AgentModelResponse(
            text.ToString(),
            model,
            provider.BuiltinId,
            inputTokens,
            outputTokens,
            toolCalls.Length == 0 ? null : toolCalls);
    }

    private static void ProcessResponsesStreamData(
        string data,
        Dictionary<string, StreamingToolCallBuffer> toolBuffers,
        StringBuilder text,
        ref string model,
        ref int? inputTokens,
        ref int? outputTokens,
        ref IReadOnlyList<AgentToolCall>? completedToolCalls,
        Action<AgentModelStreamChunk> onChunk,
        AgentProviderSettings provider)
    {
        if (data == "[DONE]")
            return;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var eventType = ReadString(root, "type");
        if (eventType == "response.output_text.delta")
        {
            var delta = ReadString(root, "delta");
            if (!string.IsNullOrEmpty(delta))
            {
                text.Append(delta);
                EnsureStreamingResponseSize(text.Length);
                onChunk(new AgentModelStreamChunk(delta));
            }
            return;
        }

        if (eventType is "response.reasoning_summary_text.delta" or "response.reasoning_text.delta")
        {
            var thinking = ReadString(root, "delta");
            if (!string.IsNullOrEmpty(thinking))
                onChunk(new AgentModelStreamChunk(Thinking: thinking));
            return;
        }

        if (eventType == "response.function_call_arguments.delta")
        {
            var key = ReadString(root, "item_id");
            if (string.IsNullOrWhiteSpace(key))
                key = ReadString(root, "call_id");
            if (string.IsNullOrWhiteSpace(key))
                key = $"responses_call_{toolBuffers.Count}";
            if (!toolBuffers.TryGetValue(key, out var buffer))
            {
                if (toolBuffers.Count >= MaximumToolCalls)
                    throw new InvalidOperationException(
                        $"Provider response contained more than {MaximumToolCalls} tool calls.");
                buffer = new StreamingToolCallBuffer(toolBuffers.Count) { Id = key };
                toolBuffers[key] = buffer;
            }
            buffer.Arguments.Append(ReadString(root, "delta"));
            return;
        }

        if (eventType is "response.output_item.added" or "response.output_item.done")
        {
            if (!root.TryGetProperty("item", out var item) ||
                item.ValueKind != JsonValueKind.Object ||
                !ReadString(item, "type").Equals("function_call", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var key = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(key))
                key = ReadString(item, "call_id");
            if (string.IsNullOrWhiteSpace(key))
                key = $"responses_call_{toolBuffers.Count}";
            if (!toolBuffers.TryGetValue(key, out var buffer))
            {
                if (toolBuffers.Count >= MaximumToolCalls)
                    throw new InvalidOperationException(
                        $"Provider response contained more than {MaximumToolCalls} tool calls.");
                buffer = new StreamingToolCallBuffer(toolBuffers.Count);
                toolBuffers[key] = buffer;
            }

            buffer.Id ??= ReadString(item, "call_id");
            buffer.Id ??= ReadString(item, "id");
            buffer.Name = ReadString(item, "name");
            var arguments = ReadString(item, "arguments");
            if (!string.IsNullOrEmpty(arguments) && buffer.Arguments.Length == 0)
                buffer.Arguments.Append(arguments);
            return;
        }

        if (eventType == "response.completed" &&
            root.TryGetProperty("response", out var completed) &&
            completed.ValueKind == JsonValueKind.Object)
        {
            var finalResponse = ParseResponsesResponse(completed.GetRawText(), provider);
            model = finalResponse.Model;
            inputTokens = finalResponse.InputTokens ?? inputTokens;
            outputTokens = finalResponse.OutputTokens ?? outputTokens;
            completedToolCalls = finalResponse.ToolCalls;
            if (text.Length == 0 && !string.IsNullOrEmpty(finalResponse.Text))
            {
                text.Append(finalResponse.Text);
                onChunk(new AgentModelStreamChunk(finalResponse.Text));
            }
        }
    }

    private static string ReadContentText(JsonElement content)
        => content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : ExtractTextParts(content);

    private static void EnsureStreamingResponseSize(int characterCount)
    {
        if (characterCount > MaximumResponseCharacters)
            throw new InvalidOperationException("Provider response is too large.");
    }

    private sealed class StreamingToolCallBuffer
    {
        public StreamingToolCallBuffer(int index)
        {
            Index = index;
        }

        public int Index { get; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private async Task<AgentModelResponse> CompleteResponsesAsync(
        AgentProviderSettings provider,
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? provider.Model.Trim() : request.Model.Trim(),
            input = request.Messages.SelectMany(ToResponsesInput).ToList(),
            stream = false,
            store = false,
            temperature = request.Temperature,
            max_output_tokens = request.MaxTokens,
            tools = request.Tools?.Select(ToResponsesTool),
            tool_choice = request.Tools is { Count: > 0 } ? "auto" : null
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProviderConfiguration.BuildResponsesUri(provider));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var apiKey = AgentProviderConfiguration.GetApiKey(provider);
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            SerializeRequest(body),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(provider.RequestTimeoutSeconds));
        try
        {
            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var responseText = await ReadResponseTextAsync(response, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw AgentProviderException.FromStatusCode(
                    (int)response.StatusCode,
                    httpRequest.RequestUri?.ToString());
            }

            try
            {
                return ParseResponsesResponse(responseText, provider);
            }
            catch (AgentProviderException)
            {
                throw;
            }
            catch (InvalidOperationException exception)
                when (IsProviderBoundaryViolation(exception))
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw AgentProviderException.Protocol(
                    "Provider returned an invalid Responses API response.",
                    exception);
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw AgentProviderException.Timeout(httpRequest.RequestUri?.ToString(), exception);
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (exception.StatusCode is { } statusCode)
                throw AgentProviderException.FromStatusCode(
                    (int)statusCode,
                    httpRequest.RequestUri?.ToString());
            throw AgentProviderException.Network(exception, httpRequest.RequestUri?.ToString());
        }
    }

    private static AgentModelResponse ParseChatResponse(
        string responseText,
        AgentProviderSettings provider)
    {
        try
        {
            return ParseResponse(responseText, provider);
        }
        catch (AgentProviderException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
            when (IsProviderBoundaryViolation(exception))
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw AgentProviderException.Protocol(
                "Provider returned an invalid Chat Completions response.",
                exception);
        }
    }

    private static AgentModelResponse ParseResponse(string responseText, AgentProviderSettings provider)
    {
        if (responseText.Length > MaximumResponseCharacters)
            throw new InvalidOperationException("Provider response is too large.");

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var choices = root.TryGetProperty("choices", out var choicesElement) &&
                      choicesElement.ValueKind == JsonValueKind.Array
            ? choicesElement
            : throw new InvalidOperationException("Provider response did not contain choices.");
        if (choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Provider response contained no choices.");

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Provider response did not contain a message.");
        }

        var content = message.TryGetProperty("content", out var contentElement)
            ? contentElement
            : default;
        var text = content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : ExtractTextParts(content);
        var toolCalls = ParseToolCalls(message);
        var model = root.TryGetProperty("model", out var modelElement) &&
                    modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString() ?? provider.Model
            : provider.Model;
        var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
        var inputTokens = ReadInt(usage, "prompt_tokens");
        var outputTokens = ReadInt(usage, "completion_tokens");

        return new AgentModelResponse(text, model, provider.BuiltinId, inputTokens, outputTokens, toolCalls);
    }

    private static object ToWireMessage(AgentChatMessage message)
    {
        var role = message.Role.Trim().ToLowerInvariant();
        if (role == "assistant" && message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role,
                content = string.IsNullOrEmpty(message.Content) ? null : message.Content,
                tool_calls = message.ToolCalls.Select(call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new
                    {
                        name = call.Name,
                        arguments = call.Arguments
                    }
                })
            };
        }

        if (role == "assistant" && !string.IsNullOrWhiteSpace(message.ToolName))
        {
            return new
            {
                role,
                content = (string?)null,
                tool_calls = new[]
                {
                    new
                    {
                        id = message.ToolCallId,
                        type = "function",
                        function = new
                        {
                            name = message.ToolName,
                            arguments = message.ToolArguments ?? "{}"
                        }
                    }
                }
            };
        }

        if (role == "tool")
        {
            return new
            {
                role,
                tool_call_id = message.ToolCallId,
                content = message.Content
            };
        }

        if (message.ContentParts is { Count: > 0 })
        {
            return new
            {
                role,
                content = ToChatContentParts(message)
            };
        }

        return new { role, content = message.Content };
    }

    private static IEnumerable<object> ToChatContentParts(AgentChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Content))
            yield return new { type = "text", text = message.Content };

        foreach (var part in message.ContentParts ?? [])
        {
            if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
            {
                yield return new
                {
                    type = "image_url",
                    image_url = new { url = BuildImageDataUrl(part) }
                };
            }
            else
            {
                yield return new
                {
                    type = "text",
                    text = FormatTextPart(part)
                };
            }
        }
    }

    private static object ToWireTool(AgentToolDefinition tool)
        => new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = tool.Parameters
            }
        };

    private static IEnumerable<object> ToResponsesInput(AgentChatMessage message)
    {
        var role = message.Role.Trim().ToLowerInvariant();
        if (role == "tool")
        {
            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                yield return new
                {
                    type = "function_call_output",
                    call_id = message.ToolCallId,
                    output = message.Content
                };
            }

            yield break;
        }

        if (role == "assistant" && message.ToolCalls is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
                yield return new { role, content = message.Content };

            foreach (var toolCall in message.ToolCalls)
            {
                yield return new
                {
                    type = "function_call",
                    call_id = toolCall.Id,
                    name = toolCall.Name,
                    arguments = toolCall.Arguments
                };
            }

            yield break;
        }

        if (role == "assistant" && !string.IsNullOrWhiteSpace(message.ToolName))
        {
            yield return new
            {
                type = "function_call",
                call_id = message.ToolCallId,
                name = message.ToolName,
                arguments = message.ToolArguments ?? "{}"
            };
            yield break;
        }

        if (message.ContentParts is { Count: > 0 })
        {
            yield return new
            {
                role = role is "system" or "user" or "assistant" ? role : "user",
                content = ToResponsesContentParts(message)
            };
            yield break;
        }

        yield return new
        {
            role = role is "system" or "user" or "assistant" ? role : "user",
            content = message.Content
        };
    }

    private static IEnumerable<object> ToResponsesContentParts(AgentChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Content))
            yield return new { type = "input_text", text = message.Content };

        foreach (var part in message.ContentParts ?? [])
        {
            if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
            {
                yield return new
                {
                    type = "input_image",
                    image_url = BuildImageDataUrl(part),
                    detail = "auto"
                };
            }
            else
            {
                yield return new
                {
                    type = "input_text",
                    text = FormatTextPart(part)
                };
            }
        }
    }

    private static string BuildImageDataUrl(AgentContentPart part)
    {
        var mediaType = string.IsNullOrWhiteSpace(part.MediaType)
            ? "image/png"
            : part.MediaType.Trim();
        return $"data:{mediaType};base64,{part.Data}";
    }

    private static string FormatTextPart(AgentContentPart part)
    {
        var filePrefix = string.IsNullOrWhiteSpace(part.FileName)
            ? string.Empty
            : $"[Attached document: {part.FileName}]\n";
        return filePrefix + (part.Text ?? string.Empty);
    }

    private static object ToResponsesTool(AgentToolDefinition tool)
        => new
        {
            type = "function",
            name = tool.Name,
            description = tool.Description,
            parameters = tool.Parameters
        };

    private static AgentModelResponse ParseResponsesResponse(
        string responseText,
        AgentProviderSettings provider)
    {
        if (responseText.Length > MaximumResponseCharacters)
            throw new InvalidOperationException("Provider response is too large.");

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var text = root.TryGetProperty("output_text", out var outputText) &&
                   outputText.ValueKind == JsonValueKind.String
            ? outputText.GetString() ?? string.Empty
            : string.Empty;
        var toolCalls = new List<AgentToolCall>();

        if (root.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            var functionCallIndex = 0;
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = ReadString(item, "type");
                if (type.Equals("message", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(text))
                {
                    text = ExtractResponsesText(item);
                    continue;
                }

                if (!type.Equals("function_call", StringComparison.OrdinalIgnoreCase))
                    continue;

                var callId = ReadString(item, "call_id");
                if (string.IsNullOrWhiteSpace(callId))
                    callId = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(callId))
                    callId = $"responses_call_{functionCallIndex}";

                var name = ReadString(item, "name");
                var arguments = ReadString(item, "arguments");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (toolCalls.Count >= MaximumToolCalls)
                    {
                        throw new InvalidOperationException(
                            $"Provider response contained more than {MaximumToolCalls} tool calls.");
                    }

                    toolCalls.Add(CreateValidatedToolCall(callId, name, arguments));
                    functionCallIndex++;
                }
            }
        }

        var model = ReadString(root, "model");
        if (string.IsNullOrWhiteSpace(model))
            model = provider.Model;
        var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;

        return new AgentModelResponse(
            text,
            model,
            provider.BuiltinId,
            ReadInt(usage, "input_tokens"),
            ReadInt(usage, "output_tokens"),
            toolCalls.Count == 0 ? null : toolCalls);
    }

    private static string ExtractResponsesText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!ReadString(item, "type").Equals("output_text", StringComparison.OrdinalIgnoreCase))
                continue;
            builder.Append(ReadString(item, "text"));
        }

        return builder.ToString();
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<AgentToolCall>? ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (calls.GetArrayLength() > MaximumToolCalls)
            throw new InvalidOperationException(
                $"Provider response contained more than {MaximumToolCalls} tool calls.");

        var parsed = new List<AgentToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object ||
                !call.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !call.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object ||
                !function.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Provider response contained an invalid tool call.");
            }

            var arguments = function.TryGetProperty("arguments", out var argumentElement) &&
                            argumentElement.ValueKind == JsonValueKind.String
                ? argumentElement.GetString() ?? "{}"
                : "{}";
            parsed.Add(CreateValidatedToolCall(
                id.GetString(),
                name.GetString(),
                arguments));
        }

        return parsed.Count == 0 ? null : parsed;
    }

    private static AgentToolCall CreateValidatedToolCall(
        string? id,
        string? name,
        string? arguments)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Provider response contained a tool call without an ID.");
        if (id.Length > AgentRunCoordinator.MaximumToolCallIdCharacters)
        {
            throw new InvalidOperationException(
                $"Provider tool call ID cannot exceed {AgentRunCoordinator.MaximumToolCallIdCharacters} characters.");
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Provider response contained a tool call without a name.");
        if (name.Length > AgentRunCoordinator.MaximumToolNameCharacters)
        {
            throw new InvalidOperationException(
                $"Provider tool name cannot exceed {AgentRunCoordinator.MaximumToolNameCharacters} characters.");
        }

        var normalizedArguments = arguments ?? "{}";
        if (normalizedArguments.Length > AgentRunCoordinator.MaximumToolArgumentsCharacters)
        {
            throw new InvalidOperationException(
                $"Provider tool arguments cannot exceed {AgentRunCoordinator.MaximumToolArgumentsCharacters} characters.");
        }

        return new AgentToolCall(id, name, normalizedArguments);
    }

    private static string SerializeRequest(object body)
    {
        var json = JsonSerializer.Serialize(
            body,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        if (Encoding.UTF8.GetByteCount(json) > MaximumRequestBytes)
        {
            throw new InvalidOperationException(
                $"Provider request cannot exceed {MaximumRequestBytes} bytes.");
        }

        return json;
    }

    private static async Task<string> ReadResponseTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > (long)MaximumResponseCharacters * 4)
            throw new InvalidOperationException("Provider response is too large.");

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024);
        var builder = new StringBuilder(Math.Min(MaximumResponseCharacters, 16 * 1024));
        var buffer = new char[16 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            if (builder.Length > MaximumResponseCharacters - read)
                throw new InvalidOperationException("Provider response is too large.");
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static string ExtractTextParts(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString());
                continue;
            }
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }

    private static int? ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.TryGetInt32(out var result)
            ? result
            : null;

    private static void ValidateRequest(
        AgentProviderSettings provider,
        AgentModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var validation = AgentProviderConfiguration.Validate(provider);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Message);

        if (request.Messages == null || request.Messages.Count == 0)
            throw new ArgumentException("At least one chat message is required.", nameof(request));
        if (request.Messages.Count > MaximumMessages)
            throw new ArgumentException($"At most {MaximumMessages} chat messages are supported.", nameof(request));
        if (request.Messages.Any(message =>
                message is null ||
                string.IsNullOrWhiteSpace(message.Role) ||
                message.Content is null ||
                message.Content.Length > MaximumMessageCharacters ||
                (!message.ContentParts?.Any() == true && string.IsNullOrWhiteSpace(message.Content)) ||
                !AreContentPartsValid(message.ContentParts) ||
                (message.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrWhiteSpace(message.ToolCallId))))
        {
            throw new ArgumentException("Chat messages contain an invalid role or are too large.", nameof(request));
        }
    }

    private static bool AreContentPartsValid(IReadOnlyList<AgentContentPart>? parts)
    {
        if (parts == null)
            return true;

        foreach (var part in parts)
        {
            if (part == null ||
                string.IsNullOrWhiteSpace(part.Type) ||
                (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase)
                    ? string.IsNullOrWhiteSpace(part.Data) ||
                      string.IsNullOrWhiteSpace(part.MediaType)
                    : string.IsNullOrWhiteSpace(part.Text)) ||
                part.Text?.Length > MaximumMessageCharacters)
            {
                return false;
            }
        }

        return true;
    }

    private static string TrimForError(string? text)
    {
        var normalized = text?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }

    private static bool IsProviderBoundaryViolation(InvalidOperationException exception)
        => exception.Message.StartsWith("Provider response is too large", StringComparison.Ordinal) ||
           exception.Message.StartsWith("Provider tool", StringComparison.Ordinal) ||
           exception.Message.StartsWith("Provider response contained more than", StringComparison.Ordinal);
}
