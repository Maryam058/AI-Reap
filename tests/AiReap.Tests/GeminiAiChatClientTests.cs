using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// GeminiAiChatClient and ResilientAiChatClient with a faked HTTP transport: no API key, no network,
// no Gemini quota. The live API is exercised only by the manual verification in docs/GEMINI_SETUP.md.
public class GeminiAiChatClientTests
{
    private const string FakeKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private static object Success(string text, string finishReason = "STOP") => new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason } },
        usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 5, totalTokenCount = 15 }
    };

    private static object Error(int code, string status, string message) => new { error = new { code, message, status } };

    private static (GeminiAiChatClient Client, FakeHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string apiKey = FakeKey,
        string? thinkingLevel = "low", int? thinkingBudget = null, double? temperature = null)
    {
        var handler = new FakeHandler(respond);
        var options = Options.Create(new GeminiOptions
        {
            ApiKey = apiKey, Model = "gemini-3.8-flash", MaxOutputTokens = 4096,
            ThinkingLevel = thinkingLevel, ThinkingBudget = thinkingBudget, Temperature = temperature
        });
        return (new GeminiAiChatClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task Sends_a_json_mode_request_with_the_key_in_a_header_never_in_the_url()
    {
        var (client, handler) = Build(_ => Json(Success("{\"actors\":[]}")));

        var result = await client.CompleteAsync("SYSTEM PROMPT", "USER PROMPT");

        Assert.Equal("{\"actors\":[]}", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", request.RequestUri!.ToString());
        Assert.DoesNotContain(FakeKey, request.RequestUri.ToString());
        Assert.Equal(FakeKey, request.Headers.GetValues("x-goog-api-key").Single());

        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal("SYSTEM PROMPT", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("USER PROMPT", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("application/json", body["generationConfig"]!["responseMimeType"]!.GetValue<string>());
        Assert.Equal(4096, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal("low", body["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
        Assert.Null(body["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]);
        // Gemini 3 should run at its default temperature unless one is explicitly configured.
        Assert.Null(body["generationConfig"]!["temperature"]);
    }

    [Fact]
    public async Task Omits_thinking_config_when_neither_level_nor_budget_is_configured()
    {
        var (client, handler) = Build(_ => Json(Success("{}")), thinkingLevel: null);
        await client.CompleteAsync("s", "u");
        Assert.Null(JsonNode.Parse(handler.Bodies.Single())!["generationConfig"]!["thinkingConfig"]);
    }

    [Fact]
    public async Task Thinking_level_wins_over_a_legacy_budget_so_both_are_never_sent()
    {
        // Gemini returns 400 when thinkingLevel and thinkingBudget appear in the same request.
        var (client, handler) = Build(_ => Json(Success("{}")), thinkingLevel: "Low", thinkingBudget: 0);
        await client.CompleteAsync("s", "u");

        var thinking = JsonNode.Parse(handler.Bodies.Single())!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal("low", thinking["thinkingLevel"]!.GetValue<string>());
        Assert.Null(thinking["thinkingBudget"]);
    }

    [Fact]
    public async Task Legacy_budget_is_sent_when_no_thinking_level_is_configured()
    {
        var (client, handler) = Build(_ => Json(Success("{}")), thinkingLevel: null, thinkingBudget: 0);
        await client.CompleteAsync("s", "u");

        var thinking = JsonNode.Parse(handler.Bodies.Single())!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(0, thinking["thinkingBudget"]!.GetValue<int>());
        Assert.Null(thinking["thinkingLevel"]);
    }

    [Fact]
    public async Task Explicitly_configured_temperature_is_sent()
    {
        var (client, handler) = Build(_ => Json(Success("{}")), temperature: 0.7);
        await client.CompleteAsync("s", "u");
        Assert.Equal(0.7, JsonNode.Parse(handler.Bodies.Single())!["generationConfig"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Missing_api_key_fails_as_not_configured_without_any_http_call()
    {
        var (client, handler) = Build(_ => Json(Success("{}")), apiKey: "");

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.NotConfigured, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Truncated_response_is_reported_as_incomplete_and_not_repairable()
    {
        var (client, _) = Build(_ => Json(Success("{\"actors\": [\"Employee\", ", "MAX_TOKENS")));

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));

        Assert.False(ex.Repairable);
        Assert.Contains("MaxOutputTokens=4096", ex.Message);
    }

    [Fact]
    public async Task Safety_block_is_reported_as_content_blocked()
    {
        var (client, _) = Build(_ => Json(new { promptFeedback = new { blockReason = "SAFETY" } }));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.ContentBlocked, ex.Kind);
    }

    [Fact]
    public async Task Empty_candidate_is_an_output_validation_failure()
    {
        var (client, _) = Build(_ => Json(new { candidates = new[] { new { content = new { parts = Array.Empty<object>() }, finishReason = "STOP" } } }));

        await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));
    }

    [Fact]
    public async Task Thought_parts_are_not_returned_as_answer_text()
    {
        var (client, _) = Build(_ => Json(new
        {
            candidates = new[]
            {
                new
                {
                    content = new { parts = new object[] { new { text = "thinking...", thought = true }, new { text = "{\"ok\":true}" } } },
                    finishReason = "STOP"
                }
            }
        }));

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
    }

    [Theory]
    [InlineData(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", AiFailureKind.Authentication, false)]
    [InlineData(403, "PERMISSION_DENIED", "Method doesn't allow unregistered callers.", AiFailureKind.Authentication, false)]
    [InlineData(429, "RESOURCE_EXHAUSTED", "Quota exceeded for metric generate_content_free_tier_requests.", AiFailureKind.RateLimited, true)]
    [InlineData(500, "INTERNAL", "An internal error has occurred.", AiFailureKind.Unavailable, true)]
    [InlineData(503, "UNAVAILABLE", "The model is overloaded. Please try again later.", AiFailureKind.Unavailable, true)]
    [InlineData(504, "DEADLINE_EXCEEDED", "Deadline expired before operation could complete.", AiFailureKind.Timeout, true)]
    [InlineData(404, "NOT_FOUND", "models/gemini-9 is not found for API version v1beta.", AiFailureKind.RequestRejected, false)]
    [InlineData(404, "NOT_FOUND", "This model models/gemini-2.5-flash is no longer available to new users.", AiFailureKind.RequestRejected, false)]
    [InlineData(400, "FAILED_PRECONDITION", "User location is not supported for the API use.", AiFailureKind.RequestRejected, false)]
    public async Task Api_errors_are_classified(int status, string grpcStatus, string message, AiFailureKind expectedKind, bool transient)
    {
        var (client, _) = Build(_ => Json(Error(status, grpcStatus, message), (HttpStatusCode)status));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(expectedKind, ex.Kind);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal((HttpStatusCode)status, ex.ProviderStatusCode);
        Assert.Equal("Gemini", ex.Provider);
    }

    [Fact]
    public async Task Rate_limit_retry_delay_is_read_from_google_retry_info()
    {
        var (client, _) = Build(_ => Json(new
        {
            error = new
            {
                code = 429, status = "RESOURCE_EXHAUSTED", message = "Quota exceeded.",
                details = new object[] { new Dictionary<string, object> { ["@type"] = "type.googleapis.com/google.rpc.RetryInfo", ["retryDelay"] = "17s" } }
            }
        }, HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
    }

    // Shaped like a real Gemini 429: QuotaFailure + Help + RetryInfo details.
    private static HttpResponseMessage RateLimited(string quotaId, string? retryDelay = "17s", string? message = null)
    {
        var details = new List<object>
        {
            new Dictionary<string, object>
            {
                ["@type"] = "type.googleapis.com/google.rpc.QuotaFailure",
                ["violations"] = new[] { new Dictionary<string, object>
                {
                    ["quotaMetric"] = "generativelanguage.googleapis.com/generate_content_free_tier_requests",
                    ["quotaId"] = quotaId,
                    ["quotaValue"] = "10"
                } }
            },
            new Dictionary<string, object> { ["@type"] = "type.googleapis.com/google.rpc.Help", ["links"] = Array.Empty<object>() }
        };
        if (retryDelay is not null)
        {
            details.Add(new Dictionary<string, object> { ["@type"] = "type.googleapis.com/google.rpc.RetryInfo", ["retryDelay"] = retryDelay });
        }

        return Json(new
        {
            error = new
            {
                code = 429, status = "RESOURCE_EXHAUSTED",
                message = message ?? "You exceeded your current quota, please check your plan and billing details.",
                details
            }
        }, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Per_minute_rate_limit_is_rate_limited_with_the_google_retry_delay_and_not_quota_exhausted()
    {
        var (client, _) = Build(_ => RateLimited("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "17s"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.ProviderStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
        Assert.False(ex.QuotaExhausted);
        Assert.StartsWith("Gemini rate limit reached.", ex.Message);
    }

    [Fact]
    public async Task Daily_quota_is_flagged_as_exhausted_and_keeps_the_retry_delay()
    {
        var (client, _) = Build(_ => RateLimited("GenerateRequestsPerDayPerProjectPerModel-FreeTier", "12s"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.True(ex.QuotaExhausted);
        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
    }

    [Theory]
    [InlineData("Quota exceeded for metric: generate_content_free_tier_requests, limit: 0, model: gemini-3.8-flash", true)]
    [InlineData("Quota exceeded for metric: generate_content_free_tier_requests, limit: 50, model: gemini-3.8-flash", false)]
    public async Task A_zero_limit_quota_is_flagged_as_exhausted(string message, bool exhausted)
    {
        var (client, _) = Build(_ => RateLimited("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", null, message));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(exhausted, ex.QuotaExhausted);
    }

    [Fact]
    public async Task Retry_after_header_in_seconds_is_preserved()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(Error(429, "RESOURCE_EXHAUSTED", "Quota exceeded."), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return response;
        });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(TimeSpan.FromSeconds(9), ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_header_as_an_http_date_is_converted_to_a_delay()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(Error(429, "RESOURCE_EXHAUSTED", "Quota exceeded."), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60));
            return response;
        });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.NotNull(ex.RetryAfter);
        Assert.InRange(ex.RetryAfter!.Value.TotalSeconds, 50, 61);
    }

    [Fact]
    public async Task A_429_without_any_retry_hint_has_no_retry_after()
    {
        var (client, _) = Build(_ => Json(Error(429, "RESOURCE_EXHAUSTED", "Resource has been exhausted."), HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Null(ex.RetryAfter);
        Assert.False(ex.QuotaExhausted);
    }

    [Fact]
    public async Task Rate_limit_errors_never_contain_the_api_key()
    {
        var (client, _) = Build(_ => RateLimited("GenerateRequestsPerDayPerProjectPerModel-FreeTier", "5s", $"Quota exceeded for key {FakeKey}."));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.DoesNotContain("AIza", ex.Message);
        Assert.DoesNotContain("AIza", ex.ProviderMessage ?? "");
    }

    // End to end through the real retry decorator: how many requests actually reach Google.
    private static ResilientAiChatClient WrapReal(GeminiAiChatClient inner, List<TimeSpan> delays) =>
        new(inner, "Gemini", new AiResilienceOptions(), NullLogger.Instance,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

    [Fact]
    public async Task Daily_quota_429_is_not_retried_so_only_one_request_reaches_gemini()
    {
        var (inner, handler) = Build(_ => RateLimited("GenerateRequestsPerDayPerProjectPerModel-FreeTier", "20s"));
        var delays = new List<TimeSpan>();

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => WrapReal(inner, delays).CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.True(ex.QuotaExhausted);
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Per_minute_429_is_retried_once_after_googles_delay_then_succeeds()
    {
        var calls = 0;
        var (inner, handler) = Build(_ => ++calls == 1
            ? RateLimited("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "4s")
            : Json(Success("{\"ok\":true}")));
        var delays = new List<TimeSpan>();

        Assert.Equal("{\"ok\":true}", await WrapReal(inner, delays).CompleteAsync("s", "u"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(delays));
    }

    [Fact]
    public async Task Persistent_429_stops_after_two_requests_instead_of_three()
    {
        var (inner, handler) = Build(_ => Json(Error(429, "RESOURCE_EXHAUSTED", "Resource has been exhausted."), HttpStatusCode.TooManyRequests));
        var delays = new List<TimeSpan>();

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => WrapReal(inner, delays).CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Embeddings_429_carries_the_retry_delay_and_quota_flag()
    {
        var handler = new FakeHandler(_ => RateLimited("EmbedContentRequestsPerDayPerProjectPerModel-FreeTier", "30s"));
        var client = new GeminiEmbeddingClient(new HttpClient(handler), Options.Create(new GeminiOptions { ApiKey = FakeKey }));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.EmbedAsync("question"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.ProviderStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.True(ex.QuotaExhausted);
        Assert.DoesNotContain("AIza", ex.Message);
    }

    [Fact]
    public async Task Error_messages_never_contain_the_api_key()
    {
        // A provider echoing the key back must not leak it into exceptions (and so logs/API responses).
        var (client, _) = Build(_ => Json(Error(400, "INVALID_ARGUMENT", $"API key not valid: {FakeKey}"), HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.DoesNotContain(FakeKey, ex.Message);
        Assert.DoesNotContain(FakeKey, ex.ProviderMessage ?? "");
        Assert.DoesNotContain("AIza", ex.Message);
    }

    [Fact]
    public async Task Network_failure_is_classified_as_unavailable()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Unavailable, ex.Kind);
    }
}

public class ResilientAiChatClientTests
{
    private sealed class ScriptedClient(params Func<CancellationToken, Task<string>>[] steps) : IAiChatClient
    {
        public int Calls { get; private set; }
        public string ModelName => "scripted";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            steps[Math.Min(Calls++, steps.Length - 1)](cancellationToken);
    }

    private static Func<CancellationToken, Task<string>> Fail(AiFailureKind kind, TimeSpan? retryAfter = null) =>
        _ => throw new AiProviderException(kind, "Test", $"simulated {kind}", retryAfter: retryAfter);

    private static Func<CancellationToken, Task<string>> Ok(string text = "{\"ok\":true}") => _ => Task.FromResult(text);

    private static (ResilientAiChatClient Client, List<TimeSpan> Delays) Wrap(IAiChatClient inner, int maxAttempts = 3, int timeoutSeconds = 30)
    {
        var delays = new List<TimeSpan>();
        var client = new ResilientAiChatClient(inner, "Test",
            new AiResilienceOptions { MaxAttempts = maxAttempts, TimeoutSeconds = timeoutSeconds, BaseDelayMilliseconds = 100, MaxDelaySeconds = 5, MaxRetryAfterSeconds = 30 },
            NullLogger.Instance,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        return (client, delays);
    }

    [Theory]
    [InlineData(AiFailureKind.Unavailable)]
    [InlineData(AiFailureKind.Timeout)]
    public async Task Transient_failures_are_retried_then_succeed(AiFailureKind kind)
    {
        var inner = new ScriptedClient(Fail(kind), Fail(kind), Ok());
        var (client, delays) = Wrap(inner);

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
        Assert.Equal(3, inner.Calls);
        Assert.Equal(2, delays.Count);
        Assert.True(delays[1] >= delays[0] - TimeSpan.FromMilliseconds(100)); // exponential (with jitter)
    }

    [Fact]
    public async Task Transient_failures_give_up_after_max_attempts_with_the_classified_error()
    {
        var inner = new ScriptedClient(Fail(AiFailureKind.Unavailable));
        var (client, _) = Wrap(inner, maxAttempts: 3);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Unavailable, ex.Kind);
        Assert.Equal(3, inner.Calls);
    }

    [Theory]
    [InlineData(AiFailureKind.Authentication)]
    [InlineData(AiFailureKind.NotConfigured)]
    [InlineData(AiFailureKind.RequestRejected)]
    [InlineData(AiFailureKind.ContentBlocked)]
    public async Task Permanent_failures_are_not_retried(AiFailureKind kind)
    {
        var inner = new ScriptedClient(Fail(kind), Ok());
        var (client, delays) = Wrap(inner);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Invalid_output_is_not_retried_here()
    {
        var inner = new ScriptedClient(_ => throw new AiOutputValidationException("bad"), Ok());
        var (client, _) = Wrap(inner);

        await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Provider_retry_after_is_honoured()
    {
        var inner = new ScriptedClient(Fail(AiFailureKind.RateLimited, TimeSpan.FromSeconds(7)), Ok());
        var (client, delays) = Wrap(inner);

        await client.CompleteAsync("s", "u");

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task A_retry_after_beyond_the_limit_fails_immediately_instead_of_holding_the_request()
    {
        // e.g. a daily free-tier quota: waiting minutes inside an HTTP request helps nobody.
        var inner = new ScriptedClient(Fail(AiFailureKind.RateLimited, TimeSpan.FromMinutes(10)), Ok());
        var (client, delays) = Wrap(inner);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Rate_limit_is_retried_once_then_succeeds()
    {
        var inner = new ScriptedClient(Fail(AiFailureKind.RateLimited), Ok());
        var (client, delays) = Wrap(inner);

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
        Assert.Equal(2, inner.Calls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Rate_limit_gives_up_after_max_rate_limit_attempts_not_max_attempts()
    {
        // Each extra attempt spends more of the same quota: 2 requests, not 3.
        var inner = new ScriptedClient(Fail(AiFailureKind.RateLimited), Fail(AiFailureKind.RateLimited), Ok());
        var (client, delays) = Wrap(inner, maxAttempts: 3);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal(2, inner.Calls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Sub_second_rate_limit_hints_wait_at_least_the_growing_backoff()
    {
        // Groq's TPM 429s hint e.g. "try again in 862.5ms"; retrying that soon hits the same window.
        var hint = TimeSpan.FromMilliseconds(10);
        var inner = new ScriptedClient(
            Fail(AiFailureKind.RateLimited, hint), Fail(AiFailureKind.RateLimited, hint), Fail(AiFailureKind.RateLimited, hint), Ok());
        var delays = new List<TimeSpan>();
        var options = new AiResilienceOptions { BaseDelayMilliseconds = 100, MaxDelaySeconds = 5, MaxRetryAfterSeconds = 30 }
            .WithRateLimitAttempts(4);
        var client = new ResilientAiChatClient(inner, "Test", options, NullLogger.Instance,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));

        Assert.Equal(4, inner.Calls);
        Assert.Equal(3, delays.Count);
        Assert.All(delays.Select((d, i) => (d, i)), x => Assert.True(x.d >= TimeSpan.FromMilliseconds(100 * Math.Pow(2, x.i))));
    }

    [Fact]
    public async Task Exhausted_quota_is_not_retried_even_with_a_short_retry_after()
    {
        var inner = new ScriptedClient(
            _ => throw new AiProviderException(AiFailureKind.RateLimited, "Test", "daily quota",
                HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3), quotaExhausted: true),
            Ok());
        var (client, delays) = Wrap(inner);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.True(ex.QuotaExhausted);
        Assert.Equal(TimeSpan.FromSeconds(3), ex.RetryAfter);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Legacy_http_429_from_other_providers_is_rate_limited_and_capped()
    {
        var inner = new ScriptedClient(_ => throw new HttpRequestException("429", null, HttpStatusCode.TooManyRequests));
        var (client, _) = Wrap(inner);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_hung_provider_times_out_per_attempt_and_is_reported_as_timeout()
    {
        var inner = new ScriptedClient(async ct => { await Task.Delay(Timeout.Infinite, ct); return ""; });
        var (client, _) = Wrap(inner, maxAttempts: 2, timeoutSeconds: 1);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Timeout, ex.Kind);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_a_timeout_or_retried()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedClient(async ct => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return ""; });
        var (client, _) = Wrap(inner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync("s", "u", cts.Token));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Legacy_http_errors_from_other_providers_are_classified()
    {
        var inner = new ScriptedClient(_ => throw new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable), Ok());
        var (client, _) = Wrap(inner);

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
        Assert.Equal(2, inner.Calls);
    }

    // ---- Ai:Gemini:TimeoutSeconds / MaxTimeoutAttempts (AiResilienceOptions.WithTimeout) ----------

    private static ResilientAiChatClient WrapWithTimeoutLimit(IAiChatClient inner, int timeoutSeconds, int maxTimeoutAttempts) =>
        new(inner, "Gemini",
            new AiResilienceOptions { MaxAttempts = 3, BaseDelayMilliseconds = 0, MaxDelaySeconds = 0 }.WithTimeout(timeoutSeconds, maxTimeoutAttempts),
            NullLogger.Instance, (_, _) => Task.CompletedTask);

    // A request that used up its whole timeout is not resent: the provider has usually processed (and
    // charged) it, and FallbackAiChatClient falls back on the Timeout instead.
    [Fact]
    public async Task A_timed_out_attempt_is_not_resent_when_MaxTimeoutAttempts_is_1()
    {
        var inner = new ScriptedClient(async ct => { await Task.Delay(Timeout.Infinite, ct); return ""; }, Ok());
        var client = WrapWithTimeoutLimit(inner, timeoutSeconds: 1, maxTimeoutAttempts: 1);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Timeout, ex.Kind);
        Assert.Equal("Gemini did not respond within 1s.", ex.Message);
        Assert.Equal(1, inner.Calls);
    }

    // The provider's own deadline (504 DEADLINE_EXCEEDED -> Timeout) follows the same rule.
    [Fact]
    public async Task A_provider_reported_timeout_is_not_resent_when_MaxTimeoutAttempts_is_1()
    {
        var inner = new ScriptedClient(Fail(AiFailureKind.Timeout), Ok());
        var client = WrapWithTimeoutLimit(inner, timeoutSeconds: 30, maxTimeoutAttempts: 1);

        Assert.Equal(AiFailureKind.Timeout, (await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"))).Kind);
        Assert.Equal(1, inner.Calls);
    }

    // Only timeouts are limited: 503s and network errors keep Ai:Resilience:MaxAttempts.
    [Fact]
    public async Task Outages_are_still_retried_up_to_MaxAttempts_under_a_timeout_limit()
    {
        var inner = new ScriptedClient(Fail(AiFailureKind.Unavailable), Fail(AiFailureKind.Unavailable), Ok());
        var client = WrapWithTimeoutLimit(inner, timeoutSeconds: 30, maxTimeoutAttempts: 1);

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
        Assert.Equal(3, inner.Calls);
    }

    // 0 keeps the previous behavior: timeouts retried like any transient failure.
    [Fact]
    public async Task MaxTimeoutAttempts_0_retries_timeouts_up_to_MaxAttempts_as_before()
    {
        var inner = new ScriptedClient(Fail(AiFailureKind.Timeout), Fail(AiFailureKind.Timeout), Ok());
        var client = WrapWithTimeoutLimit(inner, timeoutSeconds: 30, maxTimeoutAttempts: 0);

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public void WithTimeout_replaces_only_the_timeout_settings()
    {
        var shared = new AiResilienceOptions
        {
            TimeoutSeconds = 120, MaxAttempts = 3, BaseDelayMilliseconds = 1000, MaxDelaySeconds = 20,
            MaxRetryAfterSeconds = 30, MaxRateLimitAttempts = 1
        };

        var gemini = shared.WithTimeout(300, 1);

        Assert.Equal((300, 1), (gemini.TimeoutSeconds, gemini.MaxTimeoutAttempts));
        Assert.Equal((3, 1000, 20, 30, 1), (gemini.MaxAttempts, gemini.BaseDelayMilliseconds, gemini.MaxDelaySeconds,
            gemini.MaxRetryAfterSeconds, gemini.MaxRateLimitAttempts));
        Assert.Equal((120, 0), (shared.TimeoutSeconds, shared.MaxTimeoutAttempts)); // the shared settings are untouched
        Assert.Equal(1, gemini.WithoutRateLimitRetry().MaxTimeoutAttempts);
        Assert.Equal(1, gemini.WithRateLimitAttempts(4).MaxTimeoutAttempts);
    }
}
