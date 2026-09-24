using System.Net;
using System.Net.Http.Json;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// AnthropicAiChatClient is never exercised by TestHost (which always substitutes the stub client),
// so it has no coverage elsewhere. This fakes the HTTP transport to test it in isolation - no real
// API key or network call needed, and no other class under test.
public class AnthropicAiChatClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly object _responseBody;
        private readonly HttpStatusCode _statusCode;

        public FakeHandler(object responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode) { Content = JsonContent.Create(_responseBody) });
    }

    private static AnthropicAiChatClient BuildClient(object responseBody, int maxTokens = 8192, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var httpClient = new HttpClient(new FakeHandler(responseBody, statusCode)) { BaseAddress = new Uri("https://api.anthropic.com/") };
        var options = Options.Create(new AnthropicOptions { ApiKey = "test-key", Model = "claude-sonnet-5", MaxTokens = maxTokens });
        return new AnthropicAiChatClient(httpClient, options);
    }

    [Fact]
    public async Task CompleteAsync_returns_the_text_content_on_a_normal_response()
    {
        var client = BuildClient(new
        {
            content = new[] { new { type = "text", text = "{\"actors\":[]}" } },
            stop_reason = "end_turn"
        });

        var result = await client.CompleteAsync("system", "user");

        Assert.Equal("{\"actors\":[]}", result);
    }

    // The real-world case this regression-tests: a large requirement source (a ~68K-character
    // uploaded document) drove a structured-extraction response long enough to hit the old 2048
    // MaxTokens default, so Anthropic cut it off mid-generation (stop_reason "max_tokens"). The
    // truncated JSON then failed AiJsonParser's strict parse, surfacing as an opaque 500 with no
    // actionable detail. This asserts the client now fails fast with the real, actionable reason
    // instead of forcing that failure through the generic JSON-parse-error path.
    [Fact]
    public async Task CompleteAsync_throws_a_specific_actionable_error_when_the_response_is_truncated_at_max_tokens()
    {
        var client = BuildClient(new
        {
            content = new[] { new { type = "text", text = "{\"actors\": [\"Employee\", \"Manager\", incomplete" } },
            stop_reason = "max_tokens"
        }, maxTokens: 2048);

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2048", ex.Message);
    }

    // The real-world case this regression-tests: the Anthropic API key configured in the running
    // environment was an unset/placeholder value, so every Analyze call failed at the HTTP layer
    // with a 401 before AI generation, parsing, or MaxTokens ever came into play. The client used
    // to rely on EnsureSuccessStatusCode(), whose message ("401 (Unauthorized)") gave no way to
    // distinguish a bad key from an Anthropic outage or a malformed request. This asserts the
    // provider's own error body - the actual reason - surfaces in the thrown exception, so it is
    // visible in server-side logs without a debugger.
    [Fact]
    public async Task CompleteAsync_throws_an_error_with_the_provider_detail_when_the_http_call_is_unauthorized()
    {
        var client = BuildClient(
            new { type = "error", error = new { type = "authentication_error", message = "invalid x-api-key" } },
            statusCode: HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid x-api-key", ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}
