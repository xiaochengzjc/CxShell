using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CxShell.Models;

namespace CxShell.Services.Agent;

public sealed record AgentWebSearchItem(
    string Title,
    string Url,
    string Snippet);

public sealed record AgentWebResult(
    bool Success,
    string Content,
    string? Url = null,
    string? Error = null,
    int? StatusCode = null);

/// <summary>
/// Small, bounded web client for Agent read-only tools. It performs an SSRF
/// guard before every request and never follows redirects automatically.
/// </summary>
public sealed class AgentWebAccess
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly Func<AgentWebSettings?> _settings;

    public AgentWebAccess(
        Func<AgentWebSettings?>? settings = null,
        HttpClient? httpClient = null)
    {
        _settings = settings ?? (() => null);
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<AgentWebResult> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var settings = GetSettings(out var settingsError);
        if (settings == null)
            return new(false, string.Empty, Error: settingsError);
        if (string.IsNullOrWhiteSpace(settings.SearxngBaseUrl))
            return new(false, string.Empty, Error: "Web search is enabled, but no SearXNG URL is configured.");
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length > 500)
            return new(false, string.Empty, Error: "The search query must contain 1 to 500 characters.");

        if (!TryCreateHttpUri(settings.SearxngBaseUrl, "/search", out var endpoint, out var uriError))
            return new(false, string.Empty, Error: uriError);

        var builder = new UriBuilder(endpoint)
        {
            Query = $"q={Uri.EscapeDataString(query.Trim())}&format=json"
        };
        endpoint = builder.Uri;
        var guard = await GuardAsync(endpoint, settings, cancellationToken).ConfigureAwait(false);
        if (guard.Error != null)
            return new(false, string.Empty, endpoint.ToString(), guard.Error);

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var boundClient = CreateBoundHttpClientIfNeeded(guard.Addresses);
            using var response = await SendAsync(endpoint, timeout.Token, boundClient).ConfigureAwait(false);
            var body = await ReadCappedAsync(response, settings.MaxResponseBytes, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, string.Empty, endpoint.ToString(),
                    $"Web search returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);

            var items = ParseSearchResults(body, settings.MaxResults);
            return new(
                true,
                JsonSerializer.Serialize(new
                {
                    query = query.Trim(),
                    results = items
                }),
                endpoint.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, string.Empty, endpoint.ToString(), "Web search timed out.");
        }
        catch (JsonException)
        {
            return new(false, string.Empty, endpoint.ToString(), "The search provider returned invalid JSON.");
        }
        catch (HttpRequestException exception)
        {
            return new(false, string.Empty, endpoint.ToString(), $"Web search failed: {exception.Message}");
        }
    }

    public async Task<AgentWebResult> FetchAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var settings = GetSettings(out var settingsError);
        if (settings == null)
            return new(false, string.Empty, Error: settingsError);
        if (!TryCreateHttpUri(url, path: null, out var endpoint, out var uriError))
            return new(false, string.Empty, Error: uriError);

        var guard = await GuardAsync(endpoint, settings, cancellationToken).ConfigureAwait(false);
        if (guard.Error != null)
            return new(false, string.Empty, endpoint.ToString(), guard.Error);

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var boundClient = CreateBoundHttpClientIfNeeded(guard.Addresses);
            using var response = await SendAsync(endpoint, timeout.Token, boundClient).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (contentType.Length > 0 && IsBinary(contentType))
                return new(false, string.Empty, endpoint.ToString(), "Binary responses are not supported.", (int)response.StatusCode);

            var body = await ReadCappedAsync(response, settings.MaxResponseBytes, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, string.Empty, endpoint.ToString(),
                    $"Web fetch returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);

            var text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? HtmlToText(body)
                : body;
            text = LimitText(text, settings.MaxFetchCharacters);
            return new(true, text, endpoint.ToString(), StatusCode: (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, string.Empty, endpoint.ToString(), "Web fetch timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new(false, string.Empty, endpoint.ToString(), $"Web fetch failed: {exception.Message}");
        }
    }

    private AgentWebSettings? GetSettings(out string error)
    {
        var settings = _settings();
        if (settings == null || !settings.Enabled)
        {
            error = "Web access is disabled in Agent settings.";
            return null;
        }

        settings.Normalize();
        error = string.Empty;
        return settings;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        CancellationToken cancellationToken,
        HttpClient? boundClient = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await (boundClient ?? _httpClient).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return timeout;
    }

    private static async Task<string> ReadCappedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 128 * 1024));
        var buffer = new byte[32 * 1024];
        while (output.Length < maximumBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var count = (int)Math.Min(read, maximumBytes - output.Length);
            output.Write(buffer, 0, count);
            if (count < read)
                break;
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static IReadOnlyList<AgentWebSearchItem> ParseSearchResults(string json, int maximumResults)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<AgentWebSearchItem>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlElement) ||
                urlElement.ValueKind != JsonValueKind.String)
                continue;

            var url = urlElement.GetString()?.Trim() ?? string.Empty;
            var title = ReadProperty(item, "title");
            var snippet = ReadProperty(item, "content");
            if (url.Length == 0)
                continue;
            items.Add(new AgentWebSearchItem(title, url, snippet));
            if (items.Count >= maximumResults)
                break;
        }

        return items;
    }

    private async Task<AgentWebGuardResult> GuardAsync(
        Uri uri,
        AgentWebSettings settings,
        CancellationToken cancellationToken)
    {
        var explicitlyAllowed = IsAllowListed(uri, settings) || settings.AllowPrivateNetwork;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            if (!explicitlyAllowed &&
                (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                 !uri.Host.Contains('.', StringComparison.Ordinal)))
                return new([], "Private or local host access is blocked by default.");

            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new([], $"Could not resolve {uri.Host}: {exception.Message}");
            }
        }

        if (addresses.Length == 0)
            return new([], "The target did not resolve to an address.");
        if (!explicitlyAllowed && !addresses.All(IsPublicAddress))
            return new([], "The target resolves to a private, loopback, link-local, or reserved address.");

        return new(addresses, null);
    }

    private HttpClient? CreateBoundHttpClientIfNeeded(IReadOnlyList<IPAddress> addresses)
        => ReferenceEquals(_httpClient, SharedHttpClient)
            ? CreateBoundHttpClient(addresses)
            : null;

    private static HttpClient CreateBoundHttpClient(IReadOnlyList<IPAddress> addresses)
    {
        var pinnedAddresses = addresses.ToArray();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // The destination was DNS-validated and pinned by ConnectCallback.
            // A system proxy would otherwise move the actual connection away
            // from that validated address.
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                Exception? lastException = null;
                foreach (var address in pinnedAddresses)
                {
                    Socket? socket = null;
                    try
                    {
                        socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        await socket.ConnectAsync(
                                new IPEndPoint(address, context.DnsEndPoint.Port),
                                cancellationToken)
                            .ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException)
                    {
                        socket?.Dispose();
                        throw;
                    }
                    catch (Exception exception)
                    {
                        socket?.Dispose();
                        lastException = exception;
                    }
                }

                throw new HttpRequestException(
                    "The validated web target could not be reached.",
                    lastException);
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed record AgentWebGuardResult(IPAddress[] Addresses, string? Error);

    private static bool IsAllowListed(Uri uri, AgentWebSettings settings)
    {
        foreach (var raw in settings.AllowedPrivateHosts.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim().Trim('/');
            if (entry.Contains("//", StringComparison.Ordinal))
                entry = entry[(entry.IndexOf("//", StringComparison.Ordinal) + 2)..];
            entry = entry.Split('/')[0];
            if (entry.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) ||
                entry.Equals($"{uri.Host}:{uri.Port}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                (bytes[0] & 0xFE) == 0xFC)
                return false;

            // Documentation-only IPv6 space must not be treated as a public
            // destination even though it is not a private-network range.
            return !(bytes[0] == 0x20 && bytes[1] == 0x01 &&
                     bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 when b[1] is >= 64 and <= 127 => false,
            169 when b[1] == 254 => false,
            172 when b[1] is >= 16 and <= 31 => false,
            192 when b[1] == 168 || (b[1] == 0 && b[2] == 0) ||
                         (b[1] == 0 && b[2] == 2) ||
                         (b[1] == 88 && b[2] == 99) => false,
            198 when b[1] is 18 or 19 => false,
            198 when b[1] == 51 && b[2] == 100 => false,
            203 when b[1] == 0 && b[2] == 113 => false,
            >= 224 => false,
            _ => true
        };
    }

    private static bool TryCreateHttpUri(string raw, string? path, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Only HTTP(S) URLs without embedded credentials are supported.";
            return false;
        }

        if (path == null)
        {
            uri = parsed;
            return true;
        }

        var basePath = parsed.AbsolutePath.TrimEnd('/');
        uri = new UriBuilder(parsed) { Path = basePath + path }.Uri;
        return true;
    }

    private static string ReadProperty(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool IsBinary(string contentType)
        => !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
           !contentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
           !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) &&
           !contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);

    private static string HtmlToText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = HtmlTagRegex.Replace(withoutScripts, " ");
        return WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ")).Trim();
    }

    private static string LimitText(string text, int maximumCharacters)
        => text.Length <= maximumCharacters
            ? text
            : text[..maximumCharacters] + $"\n\n[truncated at {maximumCharacters} characters]";

    private static HttpClient CreateHttpClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
}
