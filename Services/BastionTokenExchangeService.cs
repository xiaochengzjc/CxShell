using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CxShell.Services;

public static class BastionTokenExchangeService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<string> ExchangeAsync(
        string token,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        token = token?.Trim() ?? string.Empty;
        endpoint = endpoint?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Token endpoint is required.", nameof(endpoint));

        using var request = CreateRequest(token, endpoint);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
            throw new InvalidOperationException($"Bastion token exchange failed: HTTP {status} {detail}");
        }

        return ExtractLaunchPayload(body);
    }

    private static HttpRequestMessage CreateRequest(string token, string endpoint)
    {
        if (endpoint.Contains("{token}", StringComparison.OrdinalIgnoreCase))
        {
            var url = endpoint.Replace("{token}", Uri.EscapeDataString(token), StringComparison.OrdinalIgnoreCase);
            return new HttpRequestMessage(HttpMethod.Get, url);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new TokenExchangeRequest(token)),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string ExtractLaunchPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Bastion token endpoint returned an empty response.");

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return body;

            if (TryGetPayload(root, "data", out var data) ||
                TryGetPayload(root, "payload", out data) ||
                TryGetPayload(root, "result", out data) ||
                TryGetPayload(root, "launch", out data))
            {
                return data;
            }
        }
        catch (JsonException)
        {
            return body;
        }

        return body;
    }

    private static bool TryGetPayload(JsonElement root, string propertyName, out string payload)
    {
        payload = string.Empty;
        if (!TryGetProperty(root, propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.String)
        {
            payload = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(payload);
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            payload = property.GetRawText();
            return true;
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement property)
    {
        foreach (var item in root.EnumerateObject())
        {
            if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = item.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private sealed record TokenExchangeRequest(string Token);
}
