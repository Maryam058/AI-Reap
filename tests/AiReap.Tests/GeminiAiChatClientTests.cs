using System.Net;
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
            ApiKey = apiKey, Model = "gemini-3.6-flash", MaxOutputTokens = 4096,
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
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent", request.RequestUri!.ToString());
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
    [InlineData(AiFailureKind.RateLimited)]
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
}
