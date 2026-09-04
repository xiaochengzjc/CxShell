using System.Net.Http.Headers;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services.Agent;

public sealed record AgentModelCatalogResult(
    bool Success,
    IReadOnlyList<string> Models,
    string? Error = null);

/// <summary>Reads a bounded OpenAI-compatible /models catalog without exposing API keys.</summary>
public sealed class AgentModelCatalogClient
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;

    public AgentModelCatalogClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<AgentModelCatalogResult> FetchAsync(
        AgentProviderSettings provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var validation = AgentProviderConfiguration.Validate(provider);
        if (!validation.IsValid)
            return new(false, [], validation.Message);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProviderConfiguration.BuildModelsUri(provider));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var apiKey = AgentProviderConfiguration.GetApiKey(provider);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(provider.RequestTimeoutSeconds, 30)));
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, [], $"Model catalog returned HTTP {(int)response.StatusCode}.");

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return new(false, [], "The provider model catalog response is too large.");

            var body = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            if (body == null)
                return new(false, [], "The provider model catalog response is too large.");

            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return new(false, [], "The provider returned an unsupported model catalog format.");

            var models = data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToArray();
            return new(true, models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, [], "Model catalog request timed out.");
        }
        catch (JsonException)
        {
            return new(false, [], "The provider returned invalid model catalog JSON.");
        }
        catch (HttpRequestException exception)
        {
            return new(false, [], $"Model catalog request failed: {exception.Message}");
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(MaximumResponseBytes, 128 * 1024));
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                return null;

            output.Write(buffer, 0, read);
        }
    }
}
