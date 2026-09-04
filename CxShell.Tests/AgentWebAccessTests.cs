using System.Net;
using System.Text;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentWebAccessTests
{
    [Fact]
    public async Task WebAccessIsDisabledByDefault()
    {
        var handler = new RecordingHttpMessageHandler(_ => TextResponse("unexpected"));
        using var client = new HttpClient(handler);
        var web = new AgentWebAccess(() => new AgentWebSettings(), client);

        var result = await web.FetchAsync("https://8.8.8.8/status");

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PrivateAddressIsBlockedUnlessExplicitlyAllowListed()
    {
        var settings = new AgentWebSettings { Enabled = true };
        var handler = new RecordingHttpMessageHandler(_ => TextResponse("private service"));
        using var client = new HttpClient(handler);
        var web = new AgentWebAccess(() => settings, client);

        var blocked = await web.FetchAsync("http://127.0.0.1:18080/status");
        Assert.False(blocked.Success);
        Assert.Contains("private", blocked.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);

        settings.AllowedPrivateHosts = "127.0.0.1:18080";
        var allowed = await web.FetchAsync("http://127.0.0.1:18080/status");

        Assert.True(allowed.Success);
        Assert.Equal("private service", allowed.Content);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("http://[2001:db8::1]/status")]
    public async Task DocumentationAndReservedAddressesAreBlocked(string rawAddress)
    {
        var settings = new AgentWebSettings { Enabled = true };
        var handler = new RecordingHttpMessageHandler(_ => TextResponse("must not reach handler"));
        using var client = new HttpClient(handler);
        var web = new AgentWebAccess(() => settings, client);
        var url = rawAddress.Contains(':', StringComparison.Ordinal)
            ? rawAddress
            : $"http://{rawAddress}/status";

        var result = await web.FetchAsync(url);

        Assert.False(result.Success);
        Assert.Contains("private", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task HtmlIsConvertedToBoundedTextAndBinaryIsRejected()
    {
        var settings = new AgentWebSettings
        {
            Enabled = true,
            MaxFetchCharacters = 2_000
        };
        var htmlResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html><script>secret noise</script><h1>Hello</h1><p>World &amp; team</p></html>",
                Encoding.UTF8,
                "text/html")
        };
        var binaryResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0, 1, 2, 3])
        };
        binaryResponse.Content.Headers.ContentType = new("application/octet-stream");
        var responses = new Queue<HttpResponseMessage>([htmlResponse, binaryResponse]);
        var handler = new RecordingHttpMessageHandler(_ => responses.Dequeue());
        using var client = new HttpClient(handler);
        var web = new AgentWebAccess(() => settings, client);

        var html = await web.FetchAsync("https://8.8.8.8/page");
        var binary = await web.FetchAsync("https://8.8.8.8/archive.bin");

        Assert.True(html.Success);
        Assert.Contains("Hello World & team", html.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret noise", html.Content, StringComparison.Ordinal);
        Assert.False(binary.Success);
        Assert.Contains("Binary", binary.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchResponseIsCapped()
    {
        var settings = new AgentWebSettings
        {
            Enabled = true,
            MaxResponseBytes = 16 * 1024,
            MaxFetchCharacters = 400_000
        };
        var handler = new RecordingHttpMessageHandler(_ => TextResponse(new string('x', 20 * 1024)));
        using var client = new HttpClient(handler);
        var web = new AgentWebAccess(() => settings, client);

        var result = await web.FetchAsync("https://8.8.8.8/large");

        Assert.True(result.Success);
        Assert.Equal(16 * 1024, result.Content.Length);
    }

    [Fact]
    public async Task SearchUsesConfiguredSearxngEndpoint()
    {
        var settings = new AgentWebSettings
        {
            Enabled = true,
            SearxngBaseUrl = "http://127.0.0.1:18080",
            AllowedPrivateHosts = "127.0.0.1:18080"
        };
        Uri? requestUri = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            return TextResponse(
                "{\"results\":[{\"title\":\"Nginx\",\"url\":\"https://nginx.org\",\"content\":\"web server\"}]}",
                "application/json");
        });
        using var client = new HttpClient(handler);
        var web = new AgentWebAccess(() => settings, client);

        var result = await web.SearchAsync("nginx config");

        Assert.True(result.Success);
        Assert.Contains("Nginx", result.Content, StringComparison.Ordinal);
        Assert.Equal("/search", requestUri?.AbsolutePath);
        Assert.Contains("q=nginx%20config", requestUri?.Query, StringComparison.Ordinal);
    }

    private static HttpResponseMessage TextResponse(string text, string mediaType = "text/plain")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, mediaType)
        };

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(_handler(request));
        }
    }
}
