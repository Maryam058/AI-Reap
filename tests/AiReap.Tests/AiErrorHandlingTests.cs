using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using Xunit;

namespace AiReap.Tests;

// The /analyze 500 investigation: an AI provider failure (the original case was Anthropic answering
// 400 "credit balance is too low") surfaced as a generic "An unexpected error occurred." These tests
// drive the real endpoint with scripted provider failures and malformed/semantically-invalid output
// (stub-backed; no live Gemini call) and assert a specific, actionable error - and that nothing is
// persisted when the AI output is rejected.
public class AiErrorHandlingTests : IAsyncLifetime
{
    private const string AnalysisMarker = "missingInformation";
    private const string GenerationMarker = "functionalRequirements";

    private readonly TestHost _host = new();
    private ApiUser _ba = null!;
    private string _projectId = null!, _sourceId = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        var (_, project) = await _ba.PostAsync("/api/projects", new { name = "AI errors", description = "d" });
        _projectId = project!["id"]!.GetValue<string>();
        var (_, source) = await _ba.PostAsync($"/api/projects/{_projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        _sourceId = source!["id"]!.GetValue<string>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private Task<(int Status, JsonNode? Body)> AnalyzeAsync() => _ba.PostAsync($"/api/requirement-sources/{_sourceId}/analyze");

    private async Task<int> ArtifactCountAsync() => (await _ba.GetAsync($"/api/projects/{_projectId}/artifacts")).Body!.AsArray().Count;

    public static TheoryData<AiFailureKind, int, string, bool> ProviderFailures => new()
    {
        { AiFailureKind.NotConfigured, 503, "ai_not_configured", false },
        { AiFailureKind.Authentication, 502, "ai_auth_failed", false },
        { AiFailureKind.RateLimited, 429, "ai_rate_limited", true },
        { AiFailureKind.Timeout, 504, "ai_timeout", true },
        { AiFailureKind.Unavailable, 503, "ai_unavailable", true },
        { AiFailureKind.RequestRejected, 502, "ai_request_rejected", false },
        { AiFailureKind.ContentBlocked, 422, "ai_content_blocked", false },
    };

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task Provider_failures_map_to_specific_problem_details_instead_of_a_generic_500(
        AiFailureKind kind, int expectedStatus, string expectedCode, bool retryable)
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(kind, "Gemini", $"simulated {kind}", HttpStatusCode.BadRequest, "provider said no"));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, body!["code"]!.GetValue<string>());
        Assert.Equal(retryable, body["retryable"]!.GetValue<bool>());
        Assert.Equal("Gemini", body["provider"]!.GetValue<string>());
        Assert.NotEqual("An unexpected error occurred.", body["title"]!.GetValue<string>());
        Assert.Equal(0, await ArtifactCountAsync());
    }

    [Fact]
    public async Task The_original_billing_rejection_now_reports_the_provider_reason()
    {
        // Exactly what the dev server logged for the original 500: real clients are wrapped in
        // ResilientAiChatClient, which classifies the provider's HttpRequestException.
        var billing = new HttpRequestException(
            "Anthropic API request failed with status 400 (BadRequest): Your credit balance is too low to access the Anthropic API.",
            null, HttpStatusCode.BadRequest);
        var classified = await Assert.ThrowsAsync<AiProviderException>(() =>
            new AiReap.Infrastructure.Ai.ResilientAiChatClient(new ThrowingClient(billing), "Anthropic",
                new AiReap.Infrastructure.Ai.AiResilienceOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                .CompleteAsync("s", "u"));
        Assert.Equal(AiFailureKind.RequestRejected, classified.Kind);

        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker, classified);
        var (status, body) = await AnalyzeAsync();

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.Contains("credit balance is too low", body["detail"]!.GetValue<string>());
    }

    // GenerationSupport's MaxChunkedParts guard refuses before anything is sent: the heading must not
    // say the provider rejected the request.
    [Fact]
    public async Task Chunk_limit_refusal_says_nothing_was_sent_instead_of_blaming_the_provider()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiChunkLimitExceededException("Groq", "The requirement source would have to be sent to Groq in 40 parts ...",
                requiredParts: 40, maxParts: 8, maxPartsSetting: "Ai:Groq:MaxChunkedParts"));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.Equal("The requirement source is too large for Groq.", body["title"]!.GetValue<string>());
        Assert.Equal(
            "The requirement source exceeds the configured Groq processing limit " +
            "(it would need 40 parts; Ai:Groq:MaxChunkedParts=8). No request was sent to Groq and nothing was saved. " +
            "Retry when an AI provider without this limit is available, or split the requirement source into smaller parts.",
            body["detail"]!.GetValue<string>());
        Assert.Equal("Groq", body["provider"]!.GetValue<string>());
        Assert.False(body["sentToProvider"]!.GetValue<bool>());
        Assert.Equal(40, body["requiredParts"]!.GetValue<int>());
        Assert.Equal(8, body["maxParts"]!.GetValue<int>());
        Assert.Equal(0, await ArtifactCountAsync());
    }

    // Groq was only the fallback: the detail leads with why Gemini did not serve the request, and the
    // body carries it in machine-readable form. Code, status and retryable stay as above.
    [Fact]
    public async Task Chunk_limit_refusal_after_a_gemini_failure_names_the_gemini_failure()
    {
        var gemini = new AiProviderException(AiFailureKind.RateLimited, "Gemini",
            "Gemini quota exhausted (daily or zero-limit quota; will not recover within this request). (HTTP 429 RESOURCE_EXHAUSTED: Quota exceeded.)",
            HttpStatusCode.TooManyRequests, "Quota exceeded.", retryAfter: TimeSpan.FromSeconds(37.2), quotaExhausted: true);
        var overBudget = new AiRequestOverBudgetException("Groq", 4000, 84_308, 8000, 8, "Ai:Groq:MaxChunkedParts")
            .WithPrimary(new PrimaryProviderOutcome("Gemini", $"Gemini failed: {gemini.Message}", gemini));
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiChunkLimitExceededException("Groq", "The requirement source would have to be sent to Groq in 40 parts ...",
                requiredParts: 40, maxParts: 8, maxPartsSetting: "Ai:Groq:MaxChunkedParts", inner: overBudget));

        var response = await _ba.Http.PostAsync($"/api/requirement-sources/{_sourceId}/analyze", null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter); // no Retry-After: retrying this request alone will not help
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.Equal("The requirement source is too large for Groq.", body["title"]!.GetValue<string>());
        Assert.Equal(
            "Gemini, the primary AI provider, did not serve this request (Gemini failed: Gemini quota exhausted (daily or zero-limit " +
            "quota; will not recover within this request). (HTTP 429 RESOURCE_EXHAUSTED: Quota exceeded.)), so it went to the fallback provider. " +
            "The requirement source exceeds the configured Groq processing limit " +
            "(it would need 40 parts; Ai:Groq:MaxChunkedParts=8). No request was sent to Groq and nothing was saved. " +
            "Retry when an AI provider without this limit is available, or split the requirement source into smaller parts.",
            body["detail"]!.GetValue<string>());
        Assert.Equal("Groq", body["provider"]!.GetValue<string>());
        Assert.False(body["sentToProvider"]!.GetValue<bool>());
        Assert.Equal("Gemini", body["primaryProvider"]!.GetValue<string>());
        Assert.Equal("RateLimited", body["primaryFailureKind"]!.GetValue<string>());
        Assert.True(body["primaryQuotaExhausted"]!.GetValue<bool>());
        Assert.Equal(38, body["primaryRetryAfterSeconds"]!.GetValue<int>());
        Assert.StartsWith("Gemini failed: Gemini quota exhausted", body["primaryOutcome"]!.GetValue<string>());
        Assert.Equal(0, await ArtifactCountAsync());
    }

    [Fact]
    public async Task Cannot_split_refusal_after_a_skipped_gemini_names_the_cool_down()
    {
        var overBudget = new AiRequestOverBudgetException("Groq", 4000, 84_308)
            .WithPrimary(new PrimaryProviderOutcome("Gemini", "Gemini was skipped (rate limit/quota cool-down until 2026-10-14 17:05:00Z)", null));
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiSourceCannotBeSplitException("Groq", "The requirement source cannot be split for Groq ...",
                inputBudget: 4000, reservedTokens: 5712, inner: overBudget));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(502, status);
        Assert.False(body!["retryable"]!.GetValue<bool>());
        Assert.StartsWith(
            "Gemini, the primary AI provider, did not serve this request (Gemini was skipped (rate limit/quota cool-down until " +
            "2026-10-14 17:05:00Z)), so it went to the fallback provider. Groq's configured input budget of 4,000 tokens",
            body["detail"]!.GetValue<string>());
        Assert.Equal("Gemini", body["primaryProvider"]!.GetValue<string>());
        Assert.Null(body["primaryFailureKind"]);
        Assert.Null(body["primaryRetryAfterSeconds"]);
    }

    // Groq's input budget is already filled by what every request must carry: refused before anything
    // is sent, and splitting the source is not offered as a remedy.
    [Fact]
    public async Task Source_that_cannot_be_split_says_nothing_was_sent_and_splitting_will_not_help()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiSourceCannotBeSplitException("Groq", "The requirement source cannot be split for Groq ...",
                inputBudget: 4000, reservedTokens: 5712));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.Equal("The requirement source cannot be sent to Groq.", body["title"]!.GetValue<string>());
        Assert.Equal(
            "Groq's configured input budget of 4,000 tokens leaves too little room for source text after " +
            "the ~5,712 tokens of instructions and context every request must carry. " +
            "No request was sent to Groq and nothing was saved. Splitting the requirement source will not help: " +
            "use an AI provider without this limit, or ask an administrator to review Ai:Groq:MaxRequestTokens.",
            body["detail"]!.GetValue<string>());
        Assert.Equal("Groq", body["provider"]!.GetValue<string>());
        Assert.False(body["sentToProvider"]!.GetValue<bool>());
        Assert.Equal(4000, body["inputBudget"]!.GetValue<int>());
        Assert.Equal(5712, body["reservedTokens"]!.GetValue<int>());
        Assert.Equal(0, await ArtifactCountAsync());
    }

    // A rejection Groq itself returned (HTTP 413) keeps the provider-rejection heading and Groq's reason.
    [Fact]
    public async Task Genuine_groq_rejection_keeps_the_provider_rejected_heading()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RequestRejected, "Groq", "Groq rejected the request.",
                HttpStatusCode.RequestEntityTooLarge, "Request too large for model."));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.Equal("The AI provider rejected the request.", body["title"]!.GetValue<string>());
        Assert.Equal("Request too large for model.", body["detail"]!.GetValue<string>());
        Assert.Null(body["sentToProvider"]);
    }

    private sealed class ThrowingClient(Exception exception) : IAiChatClient
    {
        public string ModelName => "throwing";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) => throw exception;
    }

    [Fact]
    public async Task Rate_limit_sets_a_retry_after_header()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RateLimited, "Gemini", "quota", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(12)));

        var response = await _ba.Http.PostAsync($"/api/requirement-sources/{_sourceId}/analyze", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), response.Headers.RetryAfter!.Delta);
    }

    // The frontend (web/src/lib/errors.ts apiErrorText) shows `title` + `detail` for ai_rate_limited,
    // so these strings are exactly what the user reads.
    [Fact]
    public async Task Rate_limit_with_retry_after_tells_the_user_how_long_to_wait()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RateLimited, "Gemini", "Gemini rate limit reached. (HTTP 429 RESOURCE_EXHAUSTED: Quota exceeded.)",
                HttpStatusCode.TooManyRequests, "Quota exceeded.", retryAfter: TimeSpan.FromSeconds(16.2)));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(429, status);
        Assert.Equal("ai_rate_limited", body!["code"]!.GetValue<string>());
        Assert.Equal("Gemini rate limit reached.", body["title"]!.GetValue<string>());
        Assert.Equal("Please wait and try again in about 17 seconds.", body["detail"]!.GetValue<string>());
        Assert.Equal(17, body["retryAfterSeconds"]!.GetValue<int>());
        Assert.False(body["quotaExhausted"]!.GetValue<bool>());
        Assert.True(body["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Rate_limit_without_retry_after_asks_the_user_to_wait()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RateLimited, "Gemini", "quota", HttpStatusCode.TooManyRequests));

        var response = await _ba.Http.PostAsync($"/api/requirement-sources/{_sourceId}/analyze", null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        Assert.Equal("Gemini rate limit reached.", body!["title"]!.GetValue<string>());
        Assert.Equal("Please wait and try again.", body["detail"]!.GetValue<string>());
        Assert.Null(body["retryAfterSeconds"]);
        Assert.Null(body["rateLimitType"]); // no limit named by the provider: none reported
    }

    // Gemini skipped (quota cool-down), Groq's daily token limit hit: the fallback's combined error
    // keeps Groq's own failure as its inner exception, and the limit it named is what the user reads.
    [Fact]
    public async Task Fallback_rate_limit_names_the_limit_the_fallback_provider_reported()
    {
        var groq = new AiProviderException(AiFailureKind.RateLimited, "Groq",
            "Groq daily quota exhausted. (HTTP 429 rate_limit_exceeded: Rate limit reached for model `openai/gpt-oss-120b` in organization `org_[redacted]` on tokens per day (TPD))",
            HttpStatusCode.TooManyRequests, "Rate limit reached ... on tokens per day (TPD)", quotaExhausted: true, rateLimitType: "TPD");
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RateLimited, "Gemini and Groq", $"Gemini was skipped. Fallback Groq also failed: {groq.Message}",
                HttpStatusCode.TooManyRequests, groq.ProviderMessage, inner: groq, quotaExhausted: true));

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(429, status);
        Assert.Equal("Gemini and Groq rate limit reached.", body!["title"]!.GetValue<string>());
        Assert.Equal(
            "Groq reported that its tokens-per-day limit (TPD) was reached. " +
            "The AI usage quota appears to be used up for now (for example a daily free-tier limit). Please wait and try again later.",
            body["detail"]!.GetValue<string>());
        Assert.Equal("TPD", body["rateLimitType"]!.GetValue<string>());
        Assert.True(body["quotaExhausted"]!.GetValue<bool>());
        Assert.Equal(0, await ArtifactCountAsync());
    }

    [Fact]
    public async Task Exhausted_quota_is_reported_without_promising_a_short_wait()
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RateLimited, "Gemini", "daily quota", HttpStatusCode.TooManyRequests,
                retryAfter: TimeSpan.FromSeconds(20), quotaExhausted: true));

        var response = await _ba.Http.PostAsync($"/api/requirement-sources/{_sourceId}/analyze", null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(20), response.Headers.RetryAfter!.Delta);
        Assert.Equal("Gemini rate limit reached.", body!["title"]!.GetValue<string>());
        Assert.Contains("quota appears to be used up", body["detail"]!.GetValue<string>());
        Assert.True(body["quotaExhausted"]!.GetValue<bool>());
        Assert.Equal(0, await ArtifactCountAsync());
    }

    [Fact]
    public async Task Copilot_rate_limit_returns_429_with_a_user_facing_message_and_no_provider_secrets()
    {
        const string key = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
        _host.Ai.ThrowOnceWhenPromptContains("Project Copilot",
            new AiProviderException(AiFailureKind.RateLimited, "Gemini",
                "Gemini rate limit reached. (HTTP 429 RESOURCE_EXHAUSTED: Quota exceeded for metric generate_content_free_tier_requests)",
                HttpStatusCode.TooManyRequests, "Quota exceeded for metric generate_content_free_tier_requests", retryAfter: TimeSpan.FromSeconds(42)));

        var response = await _ba.Http.PostAsJsonAsync($"/api/projects/{_projectId}/copilot/ask", new { question = "What is still open?" });
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(raw);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), response.Headers.RetryAfter!.Delta);
        Assert.Equal("ai_rate_limited", body!["code"]!.GetValue<string>());
        Assert.Equal("Gemini rate limit reached.", body["title"]!.GetValue<string>());
        Assert.Equal("Please wait and try again in about 42 seconds.", body["detail"]!.GetValue<string>());
        Assert.DoesNotContain(key, raw);
        Assert.DoesNotContain("x-goog-api-key", raw);
        Assert.DoesNotContain("generate_content_free_tier_requests", raw);
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(90, "90 seconds")]
    [InlineData(600, "10 minutes")]
    [InlineData(20_000, "6 hours")]
    public async Task Retry_after_is_rendered_in_readable_units(int seconds, string expected)
    {
        _host.Ai.ThrowOnceWhenPromptContains(AnalysisMarker,
            new AiProviderException(AiFailureKind.RateLimited, "Gemini", "quota", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(seconds)));

        var (_, body) = await AnalyzeAsync();

        Assert.Equal($"Please wait and try again in about {expected}.", body!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_json_is_repaired_once_and_then_succeeds()
    {
        _host.Ai.RespondOnceWhenPromptContains(AnalysisMarker, "{ \"actors\": [\"Employee\", ");

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(200, status);
        Assert.NotEmpty(body!["clarificationQuestions"]!.AsArray());
        Assert.Contains(_host.Ai.UserPrompts, p => p.Contains("rejected by automatic validation"));
    }

    [Fact]
    public async Task Output_that_stays_invalid_after_the_repair_attempt_is_rejected_with_details_and_nothing_is_saved()
    {
        const string invalid = """{"actors":["Employee"],"capabilities":[],"dataElements":[],"notes":[],"missingInformation":[{"topic":"","question":"","reason":"x"}]}""";
        _host.Ai.RespondOnceWhenPromptContains(AnalysisMarker, invalid);
        _host.Ai.RespondOnceWhenPromptContains(AnalysisMarker, invalid);

        var (status, body) = await AnalyzeAsync();

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", body!["code"]!.GetValue<string>());
        var errors = body["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains(errors, e => e.Contains("missingInformation[0].topic"));
        Assert.Contains(errors, e => e.Contains("missingInformation[0].question"));
        Assert.Equal(0, await ArtifactCountAsync());
    }

    [Fact]
    public async Task Semantically_invalid_requirements_are_rejected_not_silently_defaulted()
    {
        // Unknown NFR category and priority used to be accepted/defaulted to Medium.
        const string invalid = """
            {"functionalRequirements":[{"title":"Submit","actor":"Employee","priority":"Urgent-ish","dependencies":[]}],
             "nonFunctionalRequirements":[{"title":"Speed","category":"Vibes","description":"fast"}]}
            """;
        _host.Ai.RespondOnceWhenPromptContains(GenerationMarker, invalid);
        _host.Ai.RespondOnceWhenPromptContains(GenerationMarker, invalid);

        var (status, body) = await _ba.PostAsync($"/api/requirement-sources/{_sourceId}/generate-requirements");

        Assert.Equal(502, status);
        var errors = body!["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains(errors, e => e.Contains("priority") && e.Contains("Urgent-ish"));
        Assert.Contains(errors, e => e.Contains("category") && e.Contains("Vibes"));
        Assert.Equal(0, await ArtifactCountAsync());
    }

    [Fact]
    public async Task References_to_requirements_that_do_not_exist_are_rejected()
    {
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{_sourceId}/generate-requirements")).Status);
        const string invented = """{"userStories":[{"title":"Story","persona":"Employee","valueStatement":"As an employee...","priority":"High","relatedRequirementCodes":["FR-999"]}]}""";
        _host.Ai.RespondOnceWhenPromptContains("userStories", invented);
        _host.Ai.RespondOnceWhenPromptContains("userStories", invented);

        var (status, body) = await _ba.PostAsync($"/api/requirement-sources/{_sourceId}/generate-user-stories");

        Assert.Equal(502, status);
        Assert.Contains(body!["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("FR-999"));
    }

    [Fact]
    public async Task Documentation_implementation_tasks_are_accepted_without_a_repair_attempt()
    {
        // Groq labelled "write the API docs" work as Documentation, which used to fail validation
        // and cost a repair request against its per-minute token limit.
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{_sourceId}/generate-requirements")).Status);
        const string tasks = """
            {"tasks":[{"title":"Document the leave request API","description":"Write API reference docs.",
             "taskType":"Documentation","priority":"Medium","dependencies":[],"suggestedRole":"Developer","estimate":null,
             "relatedRequirementCodes":["FR-001"]}]}
            """;
        _host.Ai.RespondOnceWhenPromptContains("taskType", tasks);
        var promptsBefore = _host.Ai.UserPrompts.Count;

        var (status, body) = await _ba.PostAsync($"/api/requirement-sources/{_sourceId}/generate-tasks");

        Assert.Equal(200, status);
        Assert.Contains("\"Documentation\"", Assert.Single(body!.AsArray())!["data"]!.ToJsonString());
        Assert.Equal(promptsBefore + 1, _host.Ai.UserPrompts.Count);
    }

    [Fact]
    public async Task Titles_longer_than_the_database_column_are_rejected_before_persistence()
    {
        var longTitle = new string('x', 301);
        var tooLong = $$"""{"functionalRequirements":[{"title":"{{longTitle}}","actor":"Employee","priority":"High"}],"nonFunctionalRequirements":[]}""";
        _host.Ai.RespondOnceWhenPromptContains(GenerationMarker, tooLong);
        _host.Ai.RespondOnceWhenPromptContains(GenerationMarker, tooLong);

        var (status, _) = await _ba.PostAsync($"/api/requirement-sources/{_sourceId}/generate-requirements");

        Assert.Equal(502, status); // previously a SQL truncation error -> 500
    }
}

public class AiJsonParserTests
{
    private sealed class Sample : IValidatableAiResponse
    {
        public string Kind { get; set; } = "";
        public List<string> Codes { get; set; } = new();

        public void Validate(AiResponseValidator v)
        {
            v.OneOf(Kind, "kind", ["positive", "negative"]);
            v.CodesExist(Codes, "codes");
        }
    }

    [Fact]
    public void Strips_markdown_fences_and_validates()
    {
        var parsed = AiJsonParser.Parse<Sample>("```json\n{\"kind\":\"Positive\",\"codes\":[\"FR-001\"]}\n```", ["FR-001"]);
        Assert.Equal("Positive", parsed.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"kind\":")]
    public void Rejects_empty_or_malformed_output(string raw)
    {
        Assert.Throws<AiOutputValidationException>(() => AiJsonParser.Parse<Sample>(raw));
    }

    [Fact]
    public void Collects_every_semantic_error()
    {
        var ex = Assert.Throws<AiOutputValidationException>(() =>
            AiJsonParser.Parse<Sample>("{\"kind\":\"sideways\",\"codes\":[\"FR-002\"]}", ["FR-001"]));

        Assert.Equal(2, ex.Errors.Count);
        Assert.True(ex.Repairable);
    }

    [Fact]
    public void Reference_checks_are_skipped_when_no_known_codes_are_supplied()
    {
        var parsed = AiJsonParser.Parse<Sample>("{\"kind\":\"negative\",\"codes\":[\"FR-777\"]}");
        Assert.Single(parsed.Codes);
    }
}
