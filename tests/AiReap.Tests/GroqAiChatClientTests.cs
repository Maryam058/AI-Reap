using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// GroqAiChatClient with a faked HTTP transport: no API key, no network, no Groq quota.
public class GroqAiChatClientTests
{
    private const string FakeKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
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

    internal static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    // The OpenAI-compatible envelope Groq returns; gpt-oss puts its reasoning in a separate field.
    internal static object Success(string content, string finishReason = "stop") => new
    {
        id = "chatcmpl-test",
        model = "openai/gpt-oss-120b",
        choices = new[] { new { index = 0, message = new { role = "assistant", content, reasoning = "thinking..." }, finish_reason = finishReason } },
        usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
    };

    private static object Error(string message, string type, string? code = null) => new { error = new { message, type, code } };

    internal static (GroqAiChatClient Client, FakeHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string apiKey = FakeKey, string? reasoningEffort = "low")
    {
        var handler = new FakeHandler(respond);
        var options = Options.Create(new GroqOptions
        {
            ApiKey = apiKey, Model = "openai/gpt-oss-120b", MaxCompletionTokens = 4096, ReasoningEffort = reasoningEffort
        });
        return (new GroqAiChatClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task Sends_an_openai_compatible_json_mode_request_with_the_key_only_in_the_authorization_header()
    {
        var (client, handler) = Build(_ => Json(Success("{\"actors\":[]}")));

        var result = await client.CompleteAsync("SYSTEM PROMPT", "USER PROMPT");

        Assert.Equal("{\"actors\":[]}", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(FakeKey, request.Headers.Authorization.Parameter);
        Assert.DoesNotContain(FakeKey, handler.Bodies.Single());

        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal("openai/gpt-oss-120b", body["model"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("SYSTEM PROMPT", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("USER PROMPT", body["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal(4096, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());
        Assert.Null(body["temperature"]);
    }

    [Fact]
    public async Task Omits_reasoning_effort_when_not_configured()
    {
        var (client, handler) = Build(_ => Json(Success("{}")), reasoningEffort: null);
        await client.CompleteAsync("s", "u");
        Assert.Null(JsonNode.Parse(handler.Bodies.Single())!["reasoning_effort"]);
    }

    [Fact]
    public void Model_name_identifies_groq_for_the_audit_trail()
    {
        var (client, _) = Build(_ => Json(Success("{}")));
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName);
    }

    [Fact]
    public async Task Structured_json_from_groq_parses_through_the_existing_pipeline_parser()
    {
        const string content = "{\"actors\":[\"User\",\"Manager\"],\"capabilities\":[\"Approve leave\"],\"notes\":\"ok\"}";
        var (client, _) = Build(_ => Json(Success(content)));

        var (raw, parsed) = await GenerationSupport.CallAiAndParseAsync<AnalysisShape>(
            client, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "RequirementAnalysis", "src-1",
            "Return JSON", "Users log in; managers approve leave.", CancellationToken.None);

        Assert.Equal(content, raw);
        Assert.Equal(["User", "Manager"], parsed.Actors);
        Assert.Equal(["Approve leave"], parsed.Capabilities);
        Assert.Equal("ok", parsed.Notes);
    }

    public sealed class AnalysisShape
    {
        public List<string> Actors { get; set; } = new();
        public List<string> Capabilities { get; set; } = new();
        public string? Notes { get; set; }
    }

    [Fact]
    public async Task Missing_key_fails_as_not_configured_without_calling_groq()
    {
        var (client, handler) = Build(_ => Json(Success("{}")), apiKey: "");

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.NotConfigured, ex.Kind);
        Assert.Contains("GROQ_API_KEY", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Per_minute_429_is_rate_limited_with_the_retry_after_header()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(Error("Rate limit reached for model `openai/gpt-oss-120b` on tokens per minute (TPM): Limit 8000, Used 7900, Requested 500. Please try again in 3s.",
                "tokens", "rate_limit_exceeded"), HttpStatusCode.TooManyRequests);
            response.Headers.Add("retry-after", "3");
            return response;
        });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.Equal("Groq", ex.Provider);
        Assert.False(ex.QuotaExhausted);
        Assert.Equal(TimeSpan.FromSeconds(3), ex.RetryAfter);
    }

    [Fact]
    public async Task Per_day_429_is_reported_as_quota_exhausted()
    {
        var (client, _) = Build(_ => Json(Error("Rate limit reached for model `openai/gpt-oss-120b` on requests per day (RPD): Limit 1000, Used 1000.",
            "requests", "rate_limit_exceeded"), HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.True(ex.QuotaExhausted);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiFailureKind.Authentication)]
    [InlineData(HttpStatusCode.InternalServerError, AiFailureKind.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiFailureKind.Unavailable)]
    [InlineData((HttpStatusCode)498, AiFailureKind.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, AiFailureKind.RequestRejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, AiFailureKind.RequestRejected)]
    public async Task Http_errors_are_classified(HttpStatusCode status, AiFailureKind expected)
    {
        var (client, _) = Build(_ => Json(Error("boom", "error"), status));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal(status, ex.ProviderStatusCode);
    }

    [Fact]
    public async Task Invalid_json_generation_is_a_repairable_output_error_not_a_provider_failure()
    {
        var (client, _) = Build(_ => Json(new { error = new { message = "Failed to generate JSON", type = "invalid_request_error", code = "json_validate_failed", failed_generation = "User text here" } },
            HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));

        Assert.True(ex.Repairable);
        Assert.DoesNotContain("User text here", ex.Message + string.Join(" ", ex.Errors));
    }

    [Fact]
    public async Task Truncated_response_is_not_repairable()
    {
        var (client, _) = Build(_ => Json(Success("{\"actors\":[", finishReason: "length")));

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));

        Assert.False(ex.Repairable);
        Assert.Contains("Ai:Groq:MaxCompletionTokens", ex.Message);
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}")]
    [InlineData("<html>not json</html>")]
    public async Task Malformed_or_empty_responses_are_output_validation_errors(string body)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        await Assert.ThrowsAsync<AiOutputValidationException>(() => client.CompleteAsync("s", "u"));
    }

    [Fact]
    public async Task Network_failure_is_unavailable()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(AiFailureKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task Error_messages_never_echo_the_api_key()
    {
        var (client, _) = Build(_ => Json(Error($"Invalid API Key {FakeKey}", "invalid_request_error", "invalid_api_key"), HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));

        Assert.DoesNotContain(FakeKey, ex.Message);
        Assert.DoesNotContain(FakeKey, ex.ProviderMessage ?? "");
        Assert.Contains("[redacted-key]", ex.ProviderMessage);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("gsk_...", false)]
    [InlineData("your-groq-api-key-goes-here-please-xxxx", false)]
    [InlineData("short", false)]
    [InlineData(FakeKey, true)]
    public void HasUsableApiKey_rejects_blank_and_placeholder_keys(string? apiKey, bool expected)
    {
        Assert.Equal(expected, new GroqOptions { ApiKey = apiKey ?? string.Empty }.HasUsableApiKey);
    }
}
