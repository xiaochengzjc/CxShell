using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CxShell.Models;
using CxShell.Services;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentProviderTests
{
    [Fact]
    public void RoutinPlanPresetUsesPlanEndpointWithoutAKey()
    {
        var settings = AgentProviderPresets.CreateRoutinPlan();

        Assert.Equal("routin-ai-plan", settings.BuiltinId);
        Assert.Equal("https://api.routin.ai/plan/v1", settings.BaseUrl);
        Assert.Equal("gpt-5.4", settings.Model);
        Assert.Equal(AgentProviderType.OpenAiResponses, settings.Type);
        Assert.Equal(300, settings.RequestTimeoutSeconds);
        Assert.Equal(
            "https://api.routin.ai/plan/v1/responses",
            AgentProviderConfiguration.BuildResponsesUri(settings).ToString());
        Assert.False(settings.Enabled);
        Assert.Equal(AgentProviderValidationStatus.Disabled,
            AgentProviderConfiguration.Validate(settings).Status);
    }

    [Fact]
    public void ApiKeyIsEncryptedAndNeverAppearsInPublicSnapshot()
    {
        var settings = AgentProviderPresets.CreateRoutinPlan();
        AgentProviderConfiguration.SetApiKey(settings, "plan-secret-key");
        settings.Enabled = true;

        Assert.NotEqual("plan-secret-key", settings.EncryptedApiKey);
        Assert.StartsWith("cxaes:", settings.EncryptedApiKey);
        Assert.Equal("plan-secret-key", AgentProviderConfiguration.GetApiKey(settings));
        var snapshot = AgentProviderConfiguration.ToSnapshot(settings);
        Assert.True(snapshot.HasApiKey);
        Assert.DoesNotContain("plan-secret-key", snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationSettingsStorePersistsAgentPlanKeyAsCiphertext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CxShell.AgentProviderTests.{Guid.NewGuid():N}");
        try
        {
            var settings = new ApplicationSettings
            {
                AgentProvider = AgentProviderPresets.CreateRoutinPlan()
            };
            settings.AgentProvider.Enabled = true;
            AgentProviderConfiguration.SetApiKey(settings.AgentProvider, "plan-secret-key");

            var store = new ApplicationSettingsStore(directory);
            store.Save(settings);

            var json = File.ReadAllText(Path.Combine(directory, "application-settings.json"));
            Assert.Contains("cxaes:", json, StringComparison.Ordinal);
            Assert.DoesNotContain("plan-secret-key", json, StringComparison.Ordinal);

            var loaded = store.Load();
            Assert.Equal("plan-secret-key", AgentProviderConfiguration.GetApiKey(loaded.AgentProvider));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://provider.example/v1", false, AgentProviderValidationStatus.InsecureBaseUrl)]
    [InlineData("http://localhost:8080/v1", false, AgentProviderValidationStatus.MissingApiKey)]
    [InlineData("https://provider.example/v1", false, AgentProviderValidationStatus.MissingApiKey)]
    public void ProviderValidationRejectsUnsafeOrIncompleteConfiguration(
        string baseUrl,
        bool allowInsecureTls,
        AgentProviderValidationStatus expected)
    {
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BaseUrl = baseUrl,
            Model = "test-model",
            AllowInsecureTls = allowInsecureTls
        };

        Assert.Equal(expected, AgentProviderConfiguration.Validate(settings).Status);
    }

    [Fact]
    public async Task OpenAiCompatibleClientSendsBearerKeyAndParsesResponse()
    {
        HttpRequestMessage? captured = null;
        var capturedBody = string.Empty;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"chatcmpl-test\",\"model\":\"gpt-test\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello from provider\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BuiltinId = "routin-ai-plan",
            BaseUrl = "https://api.example/v1",
            Model = "gpt-test"
        };
        AgentProviderConfiguration.SetApiKey(settings, "secret-key");

        var response = await client.CompleteAsync(
            settings,
            new AgentModelRequest([new AgentChatMessage("user", "hello")]));

        Assert.Equal("hello from provider", response.Text);
        Assert.Equal("gpt-test", response.Model);
        Assert.Equal(12, response.InputTokens);
        Assert.Equal(7, response.OutputTokens);
        Assert.Equal("https://api.example/v1/chat/completions", captured?.RequestUri?.ToString());
        Assert.Equal("Bearer", captured?.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", captured?.Headers.Authorization?.Parameter);
        Assert.DoesNotContain("secret-key", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleClientStreamsChatTextAndToolArguments()
    {
        HttpRequestMessage? captured = null;
        var capturedBody = string.Empty;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            var firstChunk = JsonSerializer.Serialize(new
            {
                model = "gpt-stream",
                choices = new[] { new { delta = new { content = "hello " } } }
            });
            var toolStartChunk = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            content = "world",
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    id = "call-1",
                                    function = new
                                    {
                                        name = "session_command",
                                        arguments = "{\"command\":\"pwd\""
                                    }
                                }
                            }
                        }
                    }
                }
            });
            var toolEndChunk = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    function = new { arguments = "}" }
                                }
                            }
                        },
                        finish_reason = "tool_calls"
                    }
                }
            });
            var sse = string.Join(
                "\n\n",
                $"data: {firstChunk}",
                $"data: {toolStartChunk}",
                $"data: {toolEndChunk}",
                "data: [DONE]");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BaseUrl = "https://api.example/v1",
            Model = "gpt-stream"
        };
        AgentProviderConfiguration.SetApiKey(settings, "secret-key");
        var chunks = new List<AgentModelStreamChunk>();

        var response = await client.CompleteStreamingAsync(
            settings,
            new AgentModelRequest([new AgentChatMessage("user", "check")]),
            chunks.Add);

        Assert.Equal("hello world", response.Text);
        var toolCall = Assert.Single(response.ToolCalls!);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("session_command", toolCall.Name);
        Assert.Equal("{\"command\":\"pwd\"}", toolCall.Arguments);
        Assert.Equal(["hello ", "world"], chunks.Select(chunk => chunk.Text));
        using var requestDocument = JsonDocument.Parse(
            capturedBody);
        Assert.True(requestDocument.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task RoutinPlanStreamingResponsesAggregatesTextAndFunctionCall()
    {
        var handler = new DelegateHttpMessageHandler(_ =>
        {
            var sse = string.Join(
                "\n\n",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"I will check.\"}",
                "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"id\":\"fc-1\",\"call_id\":\"call-1\",\"name\":\"session_command\",\"arguments\":\"\"}}",
                "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc-1\",\"delta\":\"{\\\"command\\\":\\\"uname -a\\\"}\"}",
                "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-plan\",\"output_text\":\"I will check.\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"session_command\",\"arguments\":\"{\\\"command\\\":\\\"uname -a\\\"}\"}],\"usage\":{\"input_tokens\":11,\"output_tokens\":5}}}",
                "data: [DONE]");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = AgentProviderPresets.CreateRoutinPlan();
        settings.Enabled = true;
        settings.BaseUrl = "https://api.example/plan/v1";
        AgentProviderConfiguration.SetApiKey(settings, "plan-key");
        var chunks = new List<AgentModelStreamChunk>();

        var response = await client.CompleteStreamingAsync(
            settings,
            new AgentModelRequest([new AgentChatMessage("user", "inspect")]),
            chunks.Add);

        Assert.Equal("I will check.", response.Text);
        Assert.Equal("gpt-plan", response.Model);
        Assert.Equal(11, response.InputTokens);
        Assert.Equal(5, response.OutputTokens);
        Assert.Equal("I will check.", Assert.Single(chunks).Text);
        var call = Assert.Single(response.ToolCalls!);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("{\"command\":\"uname -a\"}", call.Arguments);
    }

    [Fact]
    public async Task OpenAiCompatibleClientSupportsContentParts()
    {
        var handler = new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"part one\"},{\"text\":\" part two\"}]}}]}",
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BaseUrl = "https://api.example/v1",
            Model = "test-model"
        };
        AgentProviderConfiguration.SetApiKey(settings, "secret-key");

        var response = await client.CompleteAsync(
            settings,
            new AgentModelRequest([new AgentChatMessage("user", "hello")]));

        Assert.Equal("part one part two", response.Text);
    }

    [Fact]
    public async Task OpenAiCompatibleClientSendsImageAndDocumentContentParts()
    {
        var capturedBody = string.Empty;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BaseUrl = "https://api.example/v1",
            Model = "vision-model"
        };
        AgentProviderConfiguration.SetApiKey(settings, "secret-key");

        await client.CompleteAsync(
            settings,
            new AgentModelRequest(
                [
                    new AgentChatMessage(
                        "user",
                        "Inspect these files.",
                        ContentParts:
                        [
                            AgentContentPart.ImagePart("image/png", "AQI=", "screen.png"),
                            AgentContentPart.TextPart("server: nginx", "notes.txt")
                        ])
                ]));

        using var document = JsonDocument.Parse(capturedBody);
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64,AQI=",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Contains("[Attached document: notes.txt]", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task RoutinPlanUsesResponsesEndpointAndParsesFunctionCalls()
    {
        HttpRequestMessage? captured = null;
        var capturedBody = string.Empty;
        var handler = new DelegateHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"resp-test\",\"model\":\"gpt-5.4\",\"output_text\":\"I will inspect the session.\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"session_command\",\"arguments\":\"{\\\"command\\\":\\\"uname -a\\\"}\"}],\"usage\":{\"input_tokens\":18,\"output_tokens\":6}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = AgentProviderPresets.CreateRoutinPlan();
        settings.Enabled = true;
        settings.BaseUrl = "https://api.example/plan/v1";
        AgentProviderConfiguration.SetApiKey(settings, "plan-key");

        var response = await client.CompleteAsync(
            settings,
            new AgentModelRequest(
                [
                    new AgentChatMessage("system", "You are an operator assistant."),
                    new AgentChatMessage("user", "Inspect the server.")
                ],
                Tools:
                [
                    new AgentToolDefinition(
                        "session_command",
                        "Run a safe command.",
                        JsonSerializer.SerializeToElement(new { type = "object" }))
                ]));

        Assert.Equal("I will inspect the session.", response.Text);
        Assert.Equal("gpt-5.4", response.Model);
        Assert.Equal(18, response.InputTokens);
        Assert.Equal(6, response.OutputTokens);
        var toolCall = Assert.Single(response.ToolCalls!);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("session_command", toolCall.Name);
        Assert.Equal("https://api.example/plan/v1/responses", captured?.RequestUri?.ToString());
        Assert.Equal("Bearer", captured?.Headers.Authorization?.Scheme);

        using var requestDocument = JsonDocument.Parse(capturedBody);
        Assert.False(requestDocument.RootElement.TryGetProperty("messages", out _));
        Assert.Equal(JsonValueKind.Array, requestDocument.RootElement.GetProperty("input").ValueKind);
        Assert.Equal("function", requestDocument.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ProviderResponseIsReadWithACharacterLimit()
    {
        var oversizedText = new string('x', 512 * 1024 + 1);
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = oversizedText
                    }
                }
            }
        });
        var handler = new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BaseUrl = "https://api.example/v1",
            Model = "test-model"
        };
        AgentProviderConfiguration.SetApiKey(settings, "secret-key");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(
            settings,
            new AgentModelRequest([new AgentChatMessage("user", "hello")]))) ;
    }

    [Fact]
    public async Task ProviderToolCallArgumentsAreBounded()
    {
        var oversizedArguments = new string('x', AgentRunCoordinator.MaximumToolArgumentsCharacters + 1);
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call-1",
                                function = new
                                {
                                    name = AgentRunCoordinator.SessionCommandToolName,
                                    arguments = oversizedArguments
                                }
                            }
                        }
                    }
                }
            }
        });
        var handler = new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleAgentModelClient(httpClient);
        var settings = new AgentProviderSettings
        {
            Enabled = true,
            BaseUrl = "https://api.example/v1",
            Model = "test-model"
        };
        AgentProviderConfiguration.SetApiKey(settings, "secret-key");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(
            settings,
            new AgentModelRequest([new AgentChatMessage("user", "hello")]))) ;
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
