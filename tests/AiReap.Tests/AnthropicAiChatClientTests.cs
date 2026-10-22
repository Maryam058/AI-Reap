using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// AnthropicAiChatClient is never exercised by TestHost (which always substitutes the stub client),
// so it has no coverage elsewhere. This fakes the HTTP transport to test it in isolation - no real
// API key or network call needed, and no other class under test.
public class AnthropicAiChatClientTests
{
    // A syntactically plausible key (long, sk-ant- prefixed, no ellipsis) that is not a real key.
    internal const string FakeKey = "sk-ant-api03-TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

        public FakeHandler(object responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
            : this((_, _) => new HttpResponseMessage(statusCode) { Content = JsonContent.Create(responseBody) })
        {
        }

        public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request, body));
            return _respond(request, body);
        }
    }

    private static AnthropicAiChatClient BuildClient(object responseBody, int maxTokens = 8192, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        BuildClient(new FakeHandler(responseBody, statusCode), new AnthropicOptions { ApiKey = FakeKey, Model = "claude-sonnet-5", MaxTokens = maxTokens });

    private static AnthropicAiChatClient BuildClient(FakeHandler handler, AnthropicOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") }, Options.Create(options));

    private static object Text(string text, string stopReason = "end_turn") =>
        new { model = "claude-sonnet-5-5", content = new[] { new { type = "text", text } }, stop_reason = stopReason };

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

        var ex = await Assert.ThrowsAsync<AiOutputTruncatedException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2048", ex.Message);
        Assert.Equal("max_tokens", ex.StopReason);
        // Asking again with the same limit cannot fix a cut-off response: no repair attempt.
        Assert.False(ex.Repairable);
    }

    // The real-world case this regression-tests: the configured key was an unset/placeholder value,
    // so every Analyze call failed with a 401 that used to read only "401 (Unauthorized)". The
    // provider's own error body must surface - now as a classified AiProviderException, so the API
    // maps it to an actionable response and the resilience policy never retries it.
    [Fact]
    public async Task CompleteAsync_throws_an_error_with_the_provider_detail_when_the_http_call_is_unauthorized()
    {
        var client = BuildClient(
            new { type = "error", error = new { type = "authentication_error", message = "invalid x-api-key" } },
            statusCode: HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("system", "user"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid x-api-key", ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.ProviderStatusCode);
        Assert.Equal(AiFailureKind.Authentication, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Equal("Anthropic", ex.Provider);
    }

    [Fact]
    public async Task Request_carries_the_configured_model_headers_and_no_sampling_or_thinking_overrides()
    {
        var handler = new FakeHandler(Text("{}"));
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey, Model = "claude-sonnet-5", MaxTokens = 16000 });

        await client.CompleteAsync("the system prompt", "the user prompt");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal(FakeKey, Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("claude-sonnet-5", json["model"]!.GetValue<string>()); // explicit override preserved
        Assert.Equal(16000, json["max_tokens"]!.GetValue<int>());
        Assert.Equal("the system prompt", json["system"]!.GetValue<string>());
        Assert.Equal("the user prompt", json["messages"]![0]!["content"]!.GetValue<string>());
        // Claude Sonnet 5.5 rejects non-default sampling values; thinking stays at the model default.
        Assert.False(json.ContainsKey("temperature"));
        Assert.False(json.ContainsKey("top_p"));
        Assert.False(json.ContainsKey("thinking"));
        Assert.False(json.ContainsKey("output_config"));
        Assert.Equal("claude-sonnet-5", client.ModelName);
    }

    [Fact]
    public async Task Configured_effort_is_sent_in_output_config()
    {
        var handler = new FakeHandler(Text("{}"));
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey, Effort = "Medium" });

        await client.CompleteAsync("s", "u");

        var json = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.Equal("medium", json["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal(AnthropicOptions.DefaultModel, json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Text_blocks_are_joined_and_thinking_blocks_are_never_part_of_the_answer()
    {
        var client = BuildClient(new
        {
            content = new object[]
            {
                new { type = "thinking", thinking = "private reasoning", signature = "sig" },
                new { type = "text", text = "{\"a\":" },
                new { type = "text", text = "1}" }
            },
            stop_reason = "end_turn"
        });

        Assert.Equal("{\"a\":1}", await client.CompleteAsync("s", "u"));
    }

    [Fact]
    public async Task Context_window_exhaustion_is_reported_as_truncation_not_accepted()
    {
        var client = BuildClient(Text("{\"partial\": tru", "model_context_window_exceeded"));

        var ex = await Assert.ThrowsAsync<AiOutputTruncatedException>(() => client.CompleteAsync("s", "u"));

        Assert.Contains("context window", ex.Message);
        Assert.Equal("model_context_window_exceeded", ex.StopReason);
        Assert.False(ex.Repairable);
    }

    [Fact]
    public async Task A_refusal_is_a_non_transient_content_block_with_its_category()
    {
        var client = BuildClient(new
        {
            content = Array.Empty<object>(),
            stop_reason = "refusal",
            stop_details = new { type = "refusal", category = "cyber", explanation = "Declined under the usage policy." }
        });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.ContentBlocked, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Contains("cyber", ex.Message);
        Assert.Contains("not used", ex.Message);
    }

    [Theory]
    [InlineData("pause_turn")]
    [InlineData("tool_use")]
    [InlineData(null)]
    public async Task An_unexpected_stop_reason_is_never_treated_as_a_complete_answer(string? stopReason)
    {
        var client = BuildClient(new { content = new[] { new { type = "text", text = "{}" } }, stop_reason = stopReason });

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));
        Assert.False(ex.Repairable);
    }

    [Fact]
    public async Task An_empty_answer_is_rejected()
    {
        var client = BuildClient(new { content = Array.Empty<object>(), stop_reason = "end_turn" });

        await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));
    }

    [Fact]
    public async Task A_malformed_response_envelope_is_rejected()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>gateway</html>") });
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey });

        await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));
    }

    public static TheoryData<int, string, string, AiFailureKind, string> ErrorCases => new()
    {
        { 400, "invalid_request_error", "prompt is too long: 1200000 tokens > 1000000 maximum", AiFailureKind.RequestRejected, "context window" },
        { 400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.", AiFailureKind.RequestRejected, "billing" },
        { 400, "invalid_request_error", "You have reached your specified workspace spend limit.", AiFailureKind.RequestRejected, "billing" },
        { 402, "billing_error", "Payment required.", AiFailureKind.RequestRejected, "billing" },
        { 403, "permission_error", "Your API key does not have permission to use the specified resource.", AiFailureKind.Authentication, "permission" },
        { 404, "not_found_error", "model: claude-sonnet-9", AiFailureKind.RequestRejected, "Ai:Anthropic:Model" },
        { 413, "request_too_large", "Request exceeds the maximum allowed number of bytes.", AiFailureKind.RequestRejected, "maximum request size" },
        { 429, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit", AiFailureKind.RateLimited, "rate limit" },
        { 500, "api_error", "Internal server error", AiFailureKind.Unavailable, "unavailable" },
        { 504, "timeout_error", "Request timed out", AiFailureKind.Timeout, "billed" },
        { 529, "overloaded_error", "Overloaded", AiFailureKind.Unavailable, "overloaded" },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Http_errors_are_classified_with_an_actionable_message(
        int status, string errorType, string providerMessage, AiFailureKind expectedKind, string expectedText)
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = JsonContent.Create(new { type = "error", error = new { type = errorType, message = providerMessage }, request_id = "req_test" })
            };
            response.Headers.Add("request-id", "req_test");
            return response;
        });
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(expectedKind, ex.Kind);
        Assert.Equal((HttpStatusCode)status, ex.ProviderStatusCode);
        Assert.Contains(expectedText, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("req_test", ex.Message);
    }

    [Fact]
    public async Task A_429_with_retry_after_carries_the_delay_and_is_retryable()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = JsonContent.Create(new { type = "error", error = new { type = "rate_limit_error", message = "slow down" } })
            };
            response.Headers.Add("retry-after", "7");
            return response;
        });
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.False(ex.QuotaExhausted);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task A_spend_cap_429_without_retry_after_is_flagged_as_exhausted_so_it_is_not_retried()
    {
        var client = BuildClient(
            new { type = "error", error = new { type = "rate_limit_error", message = "You have reached your monthly spend cap." } },
            statusCode: HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.True(ex.QuotaExhausted);
    }

    [Fact]
    public async Task Provider_messages_never_echo_the_api_key()
    {
        var client = BuildClient(
            new { type = "error", error = new { type = "authentication_error", message = $"invalid x-api-key {FakeKey}" } },
            statusCode: HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.DoesNotContain(FakeKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, ex.ProviderMessage!, StringComparison.Ordinal);
        Assert.Contains("[redacted-key]", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sk-ant-...")]
    public async Task Without_a_usable_key_nothing_is_sent_and_the_error_names_the_setting(string key)
    {
        var handler = new FakeHandler(Text("{}"));
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = key });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.NotConfigured, ex.Kind);
        Assert.Contains("Ai:Anthropic:ApiKey", ex.Message);
        Assert.Empty(handler.Requests); // proven: the handler was never called
    }

    [Fact]
    public async Task A_network_failure_is_unavailable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_cancellation_through_the_resilience_wrapper()
    {
        var handler = new FakeHandler(Text("{}"));
        var resilient = new ResilientAiChatClient(
            BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey }), "Anthropic", new AiResilienceOptions(),
            NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resilient.CompleteAsync("s", "u", cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Our_own_timeout_is_classified_as_a_timeout_and_with_one_timeout_attempt_is_not_resent()
    {
        var handler = new FakeHandler((_, _) => throw new TaskCanceledException("simulated slow provider"));
        var resilient = new ResilientAiChatClient(
            BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey }), "Anthropic",
            new AiResilienceOptions().WithTimeout(timeoutSeconds: 600, maxTimeoutAttempts: 1), NullLogger.Instance,
            delay: (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => resilient.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Timeout, ex.Kind);
        Assert.Single(handler.Requests); // a timed-out request may already be billed: never resent automatically
    }

    [Fact]
    public async Task Authentication_failures_are_never_retried()
    {
        var handler = new FakeHandler(new { type = "error", error = new { type = "authentication_error", message = "invalid x-api-key" } },
            HttpStatusCode.Unauthorized);
        var resilient = new ResilientAiChatClient(
            BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey }), "Anthropic", new AiResilienceOptions(), NullLogger.Instance,
            delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<AiProviderException>(() => resilient.CompleteAsync("s", "u"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CountInputTokensAsync_posts_the_same_request_shape_to_count_tokens()
    {
        var handler = new FakeHandler(new { input_tokens = 4321 });
        var client = BuildClient(handler, new AnthropicOptions { ApiKey = FakeKey, Model = "claude-sonnet-5-5" });

        var count = await client.CountInputTokensAsync("sys", "usr");

        Assert.Equal(4321, count);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/messages/count_tokens", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("claude-sonnet-5-5", json["model"]!.GetValue<string>());
        Assert.Equal("usr", json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.False(json.ContainsKey("max_tokens"));
    }
}
