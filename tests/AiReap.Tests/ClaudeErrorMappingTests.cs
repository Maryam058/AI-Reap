using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiReap.Api.ErrorHandling;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AiReap.Tests.ClaudeLargeSrsPipelineTests;

namespace AiReap.Tests;

// ApplicationExceptionHandler's API responses for the Claude-only route, exercised at unit level (the
// GlobalExceptionHandlerTests pattern): no WebApplicationFactory, no SQL Server, no network.
public class ClaudeErrorMappingTests
{
    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddProblemDetails();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.Path = "/api/requirement-sources/x/generate";
        context.Response.Body = new MemoryStream();

        var handled = await new ApplicationExceptionHandler(NullLogger<ApplicationExceptionHandler>.Instance)
            .TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }

    private static string Detail(JsonElement body) => body.GetProperty("detail").GetString()!;

    // ---- repair claims ------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_output_without_a_repair_attempt_does_not_claim_one()
    {
        var (status, body) = await HandleAsync(new AiOutputValidationException("The Anthropic API returned no text content."));

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", body.GetProperty("code").GetString());
        Assert.DoesNotContain("correction attempt", Detail(body).Replace("no automatic correction was attempted", ""));
        Assert.Contains("no automatic correction was attempted", Detail(body));
        Assert.Contains("returned no text content", Detail(body));
    }

    [Fact]
    public async Task Invalid_output_after_the_real_repair_attempt_says_a_correction_was_tried()
    {
        // RepairAttempted is set only by GenerationSupport, so produce the failure through the real pipeline.
        await using var claude = new ClaudeStack(Answering(_ => Ok("not json")));
        var (db, source) = Seed(AiReap.Tests.SrsEval.GoldenFixture.Source(AiReap.Tests.SrsEval.GoldenFixture.ClinicBooking));
        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));
        Assert.True(ex.RepairAttempted);
        Assert.Equal(2, claude.MessageRequests.Count);

        var (_, body) = await HandleAsync(ex);

        Assert.Contains("even after one automatic correction attempt", Detail(body));
    }

    [Fact]
    public async Task Truncated_output_is_reported_as_cut_off_and_not_retryable()
    {
        var ex = new AiOutputTruncatedException("The AI response was truncated at the configured MaxTokens limit (Ai:Anthropic:MaxTokens=16000).",
            ["Response truncated at the output-token limit (stop_reason max_tokens)."], "max_tokens");

        var (_, body) = await HandleAsync(ex);

        Assert.Contains("Ai:Anthropic:MaxTokens=16000", Detail(body));
        Assert.DoesNotContain("correction attempt", Detail(body));
        Assert.False(body.GetProperty("retryable").GetBoolean());
    }

    // ---- actionable Anthropic summaries reach the client --------------------------------------

    private static async Task<AiProviderException> AnthropicErrorAsync(int status, string type, string message)
    {
        var handler = new AnthropicAiChatClientTests.FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = JsonContent.Create(new { type = "error", error = new { type, message } })
            };
            response.Headers.Add("request-id", "req_map");
            return response;
        });
        var client = new AnthropicAiChatClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") },
            Options.Create(new AnthropicOptions { ApiKey = AnthropicAiChatClientTests.FakeKey }));
        return await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("s", "u"));
    }

    [Fact]
    public async Task A_billing_rejection_shows_the_actionable_summary_and_request_id_without_the_key()
    {
        var ex = await AnthropicErrorAsync(400, "invalid_request_error", $"Your credit balance is too low. {AnthropicAiChatClientTests.FakeKey}");

        var (status, body) = await HandleAsync(ex);

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body.GetProperty("code").GetString());
        Assert.Contains("billing, credit or spend-limit problem", Detail(body));
        Assert.Contains("Claude Console", Detail(body));
        Assert.Contains("req_map", Detail(body));
        Assert.DoesNotContain(AnthropicAiChatClientTests.FakeKey, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_model_names_the_setting_to_fix()
    {
        var (_, body) = await HandleAsync(await AnthropicErrorAsync(404, "not_found_error", "model: claude-sonnet-9"));

        Assert.Contains("Ai:Anthropic:Model", Detail(body));
    }

    [Fact]
    public async Task An_oversized_prompt_explains_the_context_window()
    {
        var (_, body) = await HandleAsync(await AnthropicErrorAsync(400, "invalid_request_error", "prompt is too long: 1200000 tokens > 1000000 maximum"));

        Assert.Contains("context window", Detail(body));
        Assert.Contains("Ai:Anthropic:MaxRequestTokens", Detail(body));
    }

    [Fact]
    public async Task A_timeout_warns_that_the_request_may_still_be_billed()
    {
        var (status, body) = await HandleAsync(new AiProviderException(AiFailureKind.Timeout, "Anthropic", "Anthropic did not respond within 600s."));

        Assert.Equal(504, status);
        Assert.Contains("may still be billed", Detail(body));
    }

    [Fact]
    public async Task An_exhausted_quota_is_described_without_free_tier_wording()
    {
        var (status, body) = await HandleAsync(new AiProviderException(AiFailureKind.RateLimited, "Anthropic",
            "Anthropic usage or spend cap reached.", HttpStatusCode.TooManyRequests, "spend cap", quotaExhausted: true));

        Assert.Equal(429, status);
        Assert.DoesNotContain("free-tier", Detail(body));
        Assert.Contains("spend limit", Detail(body));
    }

    // ---- no "another provider" advice when Claude is the only provider ---------------------------

    private const string OtherProvider = "AI provider without this limit";

    [Fact]
    public async Task A_stage_refusal_points_to_the_budget_setting_not_to_another_provider()
    {
        var ex = new AiStageOverBudgetException("UserStoryGeneration", repairAttempt: false,
            new AiRequestOverBudgetException("Anthropic", 153_333, 160_000, 200_000, 8, "Ai:Anthropic:MaxChunkedParts"));

        var (_, body) = await HandleAsync(ex);

        Assert.DoesNotContain(OtherProvider, Detail(body));
        Assert.Contains("Ai:Anthropic:MaxRequestTokens", Detail(body));
        Assert.False(body.GetProperty("sentToProvider").GetBoolean());
    }

    [Fact]
    public async Task A_part_limit_refusal_points_to_its_setting_not_to_another_provider()
    {
        var ex = new AiChunkLimitExceededException("Anthropic", "The requirement source would have to be sent to Anthropic in 30 parts ...",
            requiredParts: 30, maxParts: 24, maxPartsSetting: "Ai:Anthropic:MaxGenerationParts");

        var (_, body) = await HandleAsync(ex);

        Assert.DoesNotContain(OtherProvider, Detail(body));
        Assert.Contains("ask an administrator to review Ai:Anthropic:MaxGenerationParts", Detail(body));
    }

    [Fact]
    public async Task A_cannot_split_refusal_points_to_the_budget_setting_not_to_another_provider()
    {
        var ex = new AiSourceCannotBeSplitException("Anthropic", "The requirement source cannot be split for Anthropic ...",
            inputBudget: 1_000, reservedTokens: 1_200);

        var (_, body) = await HandleAsync(ex);

        Assert.DoesNotContain(OtherProvider, Detail(body));
        Assert.Contains("ask an administrator to review Ai:Anthropic:MaxRequestTokens", Detail(body));
    }

    [Fact]
    public async Task With_a_real_primary_and_fallback_the_previous_advice_is_kept()
    {
        var overBudget = new AiRequestOverBudgetException("Groq", 4000, 84_308, 8000, 8, "Ai:Groq:MaxChunkedParts")
            .WithPrimary(new PrimaryProviderOutcome("Gemini", "Gemini failed: quota", null));
        var ex = new AiChunkLimitExceededException("Groq", "...", requiredParts: 40, maxParts: 8,
            maxPartsSetting: "Ai:Groq:MaxChunkedParts", inner: overBudget);

        var (_, body) = await HandleAsync(ex);

        Assert.Contains(OtherProvider, Detail(body));
    }

    // ---- staged generation failures keep their part label ----------------------------------------

    [Fact]
    public async Task A_failed_staged_part_is_named_in_the_response()
    {
        var ex = new AiOutputValidationException("Part 2 of 3 of the requirement source defines requirement IDs (FR-101) but ...",
            ["x"], repairable: false);
        ex.Data[AiReap.Application.Ai.Pipeline.GenerationSupport.FailedPartKey] = 2;
        ex.Data[AiReap.Application.Ai.Pipeline.GenerationSupport.PartCountKey] = 3;

        var (_, body) = await HandleAsync(ex);

        Assert.Equal(2, body.GetProperty("failedPart").GetInt32());
        Assert.StartsWith("Part 2 of 3", Detail(body));
    }

    // ---- H1: a staged part whose requirements are missing -----------------------------------------

    private static int NothingWasSaved(JsonElement body) =>
        System.Text.RegularExpressions.Regex.Matches(Detail(body), "nothing was saved",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

    [Fact]
    public async Task A_part_with_missing_requirements_maps_to_a_non_retryable_invalid_output_naming_part_ids_and_requests()
    {
        // Produced by the real pipeline, so the mapped message is the one users will see.
        await using var claude = new ClaudeStack(Answering(r =>
        {
            var prompt = UserPrompt(r);
            return Ok(prompt.Contains("FR-250:", StringComparison.Ordinal)
                ? "{\"functionalRequirements\":[],\"nonFunctionalRequirements\":[]}"
                : ClaudeStagedGenerationTests.Faithful(prompt));
        }));
        var (db, source) = Seed(ClaudeStagedGenerationTests.Srs(400));
        var ex = await Assert.ThrowsAsync<AiStagedPartIncompleteException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        var (status, body) = await HandleAsync(ex);

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.Equal(ex.Message, Detail(body));                  // no "Part N failed; nothing was saved." prefix added
        Assert.Equal(1, NothingWasSaved(body));
        Assert.DoesNotContain("correction attempt", Detail(body));
        var part = Assert.Single(body.GetProperty("failedParts").EnumerateArray().ToList());
        Assert.Equal(body.GetProperty("failedPart").GetInt32(), part.GetProperty("part").GetInt32());
        Assert.Equal(body.GetProperty("partCount").GetInt32(), part.GetProperty("partCount").GetInt32());
        Assert.Contains("FR-250", Assert.Single(ex.Parts).MissingIds);
        Assert.Equal(ex.Parts[0].MissingIds.Take(50), part.GetProperty("missingIds").EnumerateArray().Select(i => i.GetString()!)); // capped
        Assert.Equal(claude.MessageRequests.Count, body.GetProperty("requestsSent").GetInt32());
        Assert.NotEmpty(body.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task Several_parts_with_missing_requirements_are_listed_without_a_single_failed_part()
    {
        StagedPartGap[] gaps = [new(1, 5, ["FR-001", "FR-002"]), new(4, 5, ["FR-090"])];
        var ex = new AiStagedPartIncompleteException(
            "2 parts of the requirement source define requirement IDs the AI generated no requirements for: part 1 of 5 (FR-001, FR-002); " +
            "part 4 of 5 (FR-090). They would be missing from the result, so the run was stopped. Nothing was saved.",
            ["a", "b"], gaps, requestsSent: 5);

        var (status, body) = await HandleAsync(ex);

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.False(body.TryGetProperty("failedPart", out _));
        Assert.Equal([1, 4], body.GetProperty("failedParts").EnumerateArray().Select(p => p.GetProperty("part").GetInt32()).ToList());
        Assert.Equal(5, body.GetProperty("requestsSent").GetInt32());
        Assert.Equal(1, NothingWasSaved(body));
    }

    [Fact]
    public async Task Other_invalid_output_keeps_its_previous_mapping()
    {
        var (_, repairable) = await HandleAsync(new AiOutputValidationException("x", ["functionalRequirements is required."]));
        var (_, final) = await HandleAsync(new AiOutputValidationException("Final problem.", ["y"], repairable: false));

        Assert.True(repairable.GetProperty("retryable").GetBoolean());
        Assert.False(repairable.TryGetProperty("failedParts", out _));
        Assert.Equal("Final problem.", Detail(final));
        Assert.False(final.GetProperty("retryable").GetBoolean());
        Assert.False(final.TryGetProperty("requestsSent", out _));
    }
}
