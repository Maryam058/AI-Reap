using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// OllamaAiChatClient is never exercised by TestHost (which always substitutes the stub client
// unless Ai:Provider=Ollama), so it has no coverage elsewhere. This fakes the HTTP transport to
// test it in isolation - no real Ollama install or network call needed, mirroring
// AnthropicAiChatClientTests' approach for the Anthropic client.
public class OllamaAiChatClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly object? _responseBody;
        private readonly HttpStatusCode _statusCode;
        private readonly Exception? _throws;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(object responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        private FakeHandler(Exception throws)
        {
            _throws = throws;
        }

        public static FakeHandler ThrowingHandler(Exception ex) => new(ex);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (_throws is not null)
            {
                throw _throws;
            }

            return new HttpResponseMessage(_statusCode) { Content = JsonContent.Create(_responseBody) };
        }
    }

    private static (OllamaAiChatClient Client, FakeHandler Handler) BuildClient(
        object responseBody, int numCtx = 8192, HttpStatusCode statusCode = HttpStatusCode.OK, string model = "llama3.1:8b")
    {
        var handler = new FakeHandler(responseBody, statusCode);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var options = Options.Create(new OllamaOptions { BaseUrl = "http://localhost:11434", Model = model, NumCtx = numCtx, TimeoutSeconds = 30 });
        return (new OllamaAiChatClient(httpClient, options), handler);
    }

    [Fact]
    public async Task CompleteAsync_sends_the_configured_model_messages_and_json_format_to_the_chat_endpoint()
    {
        var (client, handler) = BuildClient(new
        {
            message = new { role = "assistant", content = "{\"actors\":[]}" },
            done = true,
            done_reason = "stop"
        }, numCtx: 4096, model: "qwen2.5:7b");

        await client.CompleteAsync("system prompt", "user prompt");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequest.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("qwen2.5:7b", root.GetProperty("model").GetString());
        Assert.Equal("json", root.GetProperty("format").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, root.GetProperty("options").GetProperty("num_ctx").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user prompt", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_returns_the_message_content_on_a_normal_response()
    {
        var (client, _) = BuildClient(new
        {
            message = new { role = "assistant", content = "{\"actors\":[]}" },
            done = true,
            done_reason = "stop"
        });

        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("{\"actors\":[]}", result);
    }

    // Mirrors AnthropicAiChatClientTests' truncation regression test: a response cut off by the
    // model's context/output limit is not valid JSON and would otherwise surface as an opaque
    // parse failure further up the pipeline instead of the real, actionable reason.
    [Fact]
    public async Task CompleteAsync_throws_a_specific_actionable_error_when_the_response_is_truncated_at_the_context_limit()
    {
        var (client, _) = BuildClient(new
        {
            message = new { role = "assistant", content = "{\"actors\": [\"Employee\", incomplete" },
            done = true,
            done_reason = "length"
        }, numCtx: 2048);

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NumCtx", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_throws_an_actionable_error_when_the_configured_model_is_not_installed()
    {
        var (client, _) = BuildClient(
            new { error = "model \"llama3.1:8b\" not found, try pulling it first" },
            statusCode: HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("not installed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ollama pull", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_throws_a_clear_error_when_ollama_is_not_running()
    {
        var handler = FakeHandler.ThrowingHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var options = Options.Create(new OllamaOptions { BaseUrl = "http://localhost:11434", Model = "llama3.1:8b" });
        var client = new OllamaAiChatClient(httpClient, options);

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("not running", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ollama pull", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_throws_when_the_response_body_is_empty()
    {
        var (client, _) = BuildClient(new { message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" });

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_throws_when_ollama_reports_an_error_in_a_200_response()
    {
        var (client, _) = BuildClient(new { error = "something went wrong", done = true });

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("something went wrong", ex.Message);
    }
}
