using System.Net;
using System.Net.Http.Json;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// Gemini-primary / Groq-fallback behavior (FallbackAiChatClient). Providers are scripted fakes or
// the real clients over a faked HTTP transport - never a live API.
public class FallbackAiChatClientTests
{
    private sealed class ScriptedClient(string modelName, params Func<string>[] outcomes) : IAiChatClient
    {
        private int _calls;
        public int Calls => _calls;
        public string ModelName => modelName;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            var outcome = outcomes[Math.Min(_calls, outcomes.Length - 1)];
            _calls++;
            return Task.FromResult(outcome());
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static Func<string> Returns(string text) => () => text;
    private static Func<string> Throws(Exception ex) => () => throw ex;

    private static AiProviderException GeminiFailure(AiFailureKind kind, HttpStatusCode? status = null, TimeSpan? retryAfter = null, bool quotaExhausted = false) =>
        new(kind, "Gemini", $"Gemini failure {kind}", status, "provider says no", retryAfter, quotaExhausted: quotaExhausted);

    private static (FallbackAiChatClient Client, ListLogger Log) Build(
        IAiChatClient gemini, IAiChatClient groq, AiProviderCooldown? cooldown = null, Func<DateTimeOffset>? clock = null)
    {
        var log = new ListLogger();
        return (new FallbackAiChatClient(gemini, "Gemini", groq, "Groq", cooldown ?? new AiProviderCooldown(),
            TimeSpan.FromMinutes(10), log, clock), log);
    }

    // 1. Gemini success
    [Fact]
    public async Task Gemini_success_is_returned_and_groq_is_never_called()
    {
        var gemini = new ScriptedClient("gemini-3.6-flash", Returns("{\"from\":\"gemini\"}"));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{\"from\":\"groq\"}"));
        var (client, _) = Build(gemini, groq);

        var result = await client.CompleteAsync("s", "u");

        Assert.Equal("{\"from\":\"gemini\"}", result);
        Assert.Equal(0, groq.Calls);
        Assert.Equal("gemini-3.6-flash", client.ModelName); // what the AI audit trail records
    }

    // 2. Gemini quota failure -> Groq
    [Theory]
    [InlineData(false)] // per-minute limit
    [InlineData(true)]  // daily / zero-limit quota
    public async Task Gemini_rate_limit_or_quota_falls_back_to_groq_and_the_audit_model_is_groq(bool quotaExhausted)
    {
        var gemini = new ScriptedClient("gemini-3.6-flash", Throws(GeminiFailure(AiFailureKind.RateLimited, HttpStatusCode.TooManyRequests, quotaExhausted: quotaExhausted)));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{\"from\":\"groq\"}"));
        var (client, log) = Build(gemini, groq);

        var result = await client.CompleteAsync("s", "u");

        Assert.Equal("{\"from\":\"groq\"}", result);
        Assert.Equal(1, gemini.Calls);
        Assert.Equal(1, groq.Calls);
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName); // never recorded as Gemini
        Assert.Contains(log.Messages, m => m.Contains("Gemini attempted"));
        Assert.Contains(log.Messages, m => m.Contains("falling back to Groq") && m.Contains("RateLimited"));
        Assert.Contains(log.Messages, m => m.Contains("Groq succeeded"));
    }

    [Fact]
    public async Task Model_name_follows_the_provider_that_served_each_call()
    {
        var gemini = new ScriptedClient("gemini-3.6-flash",
            Throws(GeminiFailure(AiFailureKind.Unavailable, HttpStatusCode.ServiceUnavailable)), Returns("{}"));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{}"));
        var (client, _) = Build(gemini, groq);

        await client.CompleteAsync("s", "u");
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName);

        await client.CompleteAsync("s", "u");
        Assert.Equal("gemini-3.6-flash", client.ModelName);
    }

    // 3. Gemini transient failure -> Groq
    [Theory]
    [InlineData(AiFailureKind.Unavailable)]
    [InlineData(AiFailureKind.Timeout)]
    public async Task Gemini_transient_outage_or_timeout_falls_back_to_groq(AiFailureKind kind)
    {
        var gemini = new ScriptedClient("gemini-3.6-flash", Throws(GeminiFailure(kind, HttpStatusCode.ServiceUnavailable)));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{\"from\":\"groq\"}"));
        var (client, _) = Build(gemini, groq);

        Assert.Equal("{\"from\":\"groq\"}", await client.CompleteAsync("s", "u"));
    }

    // 4. Gemini normal failure -> no blind fallback
    [Theory]
    [InlineData(AiFailureKind.Authentication)]
    [InlineData(AiFailureKind.NotConfigured)]
    [InlineData(AiFailureKind.RequestRejected)]
    [InlineData(AiFailureKind.ContentBlocked)]
    public async Task Configuration_request_and_content_errors_do_not_fall_back(AiFailureKind kind)
    {
        var failure = GeminiFailure(kind, HttpStatusCode.BadRequest);
        var gemini = new ScriptedClient("gemini-3.6-flash", Throws(failure));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{}"));
        var (client, _) = Build(gemini, groq);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Same(failure, ex);
        Assert.Equal(0, groq.Calls);
    }

    [Fact]
    public async Task Invalid_output_and_programming_errors_do_not_fall_back()
    {
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{}"));

        var (invalidOutput, _) = Build(new ScriptedClient("gemini", Throws(new AiOutputValidationException("bad json"))), groq);
        await Assert.ThrowsAsync<AiOutputValidationException>(() => invalidOutput.CompleteAsync("s", "u"));

        var (bug, _) = Build(new ScriptedClient("gemini", Throws(new InvalidOperationException("bug"))), groq);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bug.CompleteAsync("s", "u"));

        var (cancelled, _) = Build(new ScriptedClient("gemini", Throws(new OperationCanceledException())), groq);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.CompleteAsync("s", "u"));

        Assert.Equal(0, groq.Calls);
    }

    // 5. Both providers unavailable
    [Fact]
    public async Task Both_rate_limited_returns_a_rate_limit_error_naming_both_providers()
    {
        var gemini = new ScriptedClient("gemini-3.6-flash",
            Throws(GeminiFailure(AiFailureKind.RateLimited, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(40))));
        var groqFailure = new AiProviderException(AiFailureKind.RateLimited, "Groq", "Groq rate limit reached.",
            HttpStatusCode.TooManyRequests, "TPM", TimeSpan.FromSeconds(7));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Throws(groqFailure));
        var (client, log) = Build(gemini, groq);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal("Gemini and Groq", ex.Provider);
        Assert.Contains("Gemini failed", ex.Message);
        Assert.Contains("Groq also failed", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter); // the sooner of the two
        Assert.False(ex.QuotaExhausted);
        Assert.Same(groqFailure, ex.InnerException);
        Assert.Contains(log.Messages, m => m.Contains("Groq also failed"));
    }

    [Fact]
    public async Task Gemini_down_and_groq_rejecting_its_key_reports_the_groq_authentication_failure()
    {
        var gemini = new ScriptedClient("gemini-3.6-flash", Throws(GeminiFailure(AiFailureKind.Unavailable, HttpStatusCode.ServiceUnavailable)));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b",
            Throws(new AiProviderException(AiFailureKind.Authentication, "Groq", "Groq rejected the configured API key.", HttpStatusCode.Unauthorized)));
        var (client, _) = Build(gemini, groq);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Authentication, ex.Kind);
        Assert.Contains("Groq", ex.Provider);
    }

    [Fact]
    public async Task Gemini_quota_cool_down_skips_gemini_on_the_next_call_then_retries_it_after_the_window()
    {
        var now = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        var cooldown = new AiProviderCooldown();
        var gemini = new ScriptedClient("gemini-3.6-flash",
            Throws(GeminiFailure(AiFailureKind.RateLimited, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30))), Returns("{\"from\":\"gemini\"}"));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{\"from\":\"groq\"}"));
        var (client, log) = Build(gemini, groq, cooldown, () => now);

        await client.CompleteAsync("s", "u");            // Gemini 429 -> Groq
        await client.CompleteAsync("s", "u");            // inside the 30s window -> Groq directly
        Assert.Equal(1, gemini.Calls);
        Assert.Equal(2, groq.Calls);
        Assert.Contains(log.Messages, m => m.Contains("Gemini skipped"));

        now = now.AddSeconds(31);
        Assert.Equal("{\"from\":\"gemini\"}", await client.CompleteAsync("s", "u"));
        Assert.Equal(2, gemini.Calls);
    }

    [Fact]
    public async Task Per_minute_429_without_a_retry_hint_does_not_start_a_cool_down()
    {
        var gemini = new ScriptedClient("gemini-3.6-flash",
            Throws(GeminiFailure(AiFailureKind.RateLimited, HttpStatusCode.TooManyRequests)), Returns("{}"));
        var groq = new ScriptedClient("groq:openai/gpt-oss-120b", Returns("{}"));
        var (client, _) = Build(gemini, groq);

        await client.CompleteAsync("s", "u");
        await client.CompleteAsync("s", "u");

        Assert.Equal(2, gemini.Calls);
    }

    // ---- End to end: real Gemini and Groq clients over fake HTTP, wrapped exactly as DI wraps them.

    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";

    private static (FallbackAiChatClient Client, GroqAiChatClientTests.FakeHandler GeminiHttp, GroqAiChatClientTests.FakeHandler GroqHttp) BuildReal(
        Func<HttpRequestMessage, HttpResponseMessage> geminiRespond, Func<HttpRequestMessage, HttpResponseMessage> groqRespond)
    {
        var geminiHttp = new GroqAiChatClientTests.FakeHandler(geminiRespond);
        var groqHttp = new GroqAiChatClientTests.FakeHandler(groqRespond);
        var gemini = new GeminiAiChatClient(new HttpClient(geminiHttp), Options.Create(new GeminiOptions { ApiKey = GeminiKey }));
        var groq = new GroqAiChatClient(new HttpClient(groqHttp), Options.Create(new GroqOptions { ApiKey = GroqKey }));
        var resilience = new AiResilienceOptions { BaseDelayMilliseconds = 0 };
        Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

        var client = new FallbackAiChatClient(
            new ResilientAiChatClient(gemini, "Gemini", resilience.WithoutRateLimitRetry(), NullLogger.Instance, NoDelay), "Gemini",
            new ResilientAiChatClient(groq, "Groq", resilience, NullLogger.Instance, NoDelay), "Groq",
            new AiProviderCooldown(), TimeSpan.FromMinutes(10), NullLogger.Instance);
        return (client, geminiHttp, groqHttp);
    }

    private static HttpResponseMessage GeminiDailyQuota() => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new
        {
            error = new
            {
                code = 429,
                message = "You exceeded your current quota. Quota exceeded for metric: generate_content_free_tier_requests, limit: 20",
                status = "RESOURCE_EXHAUSTED",
                details = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["@type"] = "type.googleapis.com/google.rpc.QuotaFailure",
                        ["violations"] = new[] { new { quotaId = "GenerateRequestsPerDayPerProjectPerModel-FreeTier" } }
                    }
                }
            }
        })
    };

    [Fact]
    public async Task Gemini_daily_quota_429_goes_to_groq_immediately_without_retrying_gemini_and_the_json_parses()
    {
        const string json = "{\"actors\":[\"Manager\"],\"capabilities\":[\"Approve leave\"],\"notes\":\"from groq\"}";
        var (client, geminiHttp, groqHttp) = BuildReal(_ => GeminiDailyQuota(), _ => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(json)));

        var (_, parsed) = await GenerationSupport.CallAiAndParseAsync<GroqAiChatClientTests.AnalysisShape>(
            client, NullLogger.Instance, "RequirementAnalysis", "src-1", "Return JSON", "text", CancellationToken.None);

        Assert.Equal("from groq", parsed.Notes);
        Assert.Single(geminiHttp.Requests); // no Gemini retry spending more quota
        Assert.Single(groqHttp.Requests);
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName);
    }

    [Fact]
    public async Task Gemini_per_minute_429_is_not_retried_either_when_a_fallback_exists()
    {
        var (client, geminiHttp, _) = BuildReal(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = JsonContent.Create(new { error = new { code = 429, message = "Resource exhausted.", status = "RESOURCE_EXHAUSTED" } })
            },
            _ => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success("{}")));

        await client.CompleteAsync("s", "u");

        Assert.Single(geminiHttp.Requests);
    }

    [Fact]
    public async Task Gemini_invalid_key_is_surfaced_and_groq_is_not_called()
    {
        var (client, _, groqHttp) = BuildReal(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = new { code = 400, message = "API key not valid. Please pass a valid API key.", status = "INVALID_ARGUMENT" } })
            },
            _ => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success("{}")));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Authentication, ex.Kind);
        Assert.Equal("Gemini", ex.Provider);
        Assert.Empty(groqHttp.Requests);
    }

    [Fact]
    public async Task Gemini_5xx_is_retried_as_configured_then_falls_back_to_groq()
    {
        var (client, geminiHttp, groqHttp) = BuildReal(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("overloaded") },
            _ => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success("{\"ok\":true}")));

        Assert.Equal("{\"ok\":true}", await client.CompleteAsync("s", "u"));
        Assert.Equal(new AiResilienceOptions().MaxAttempts, geminiHttp.Requests.Count);
        Assert.Single(groqHttp.Requests);
    }
}
