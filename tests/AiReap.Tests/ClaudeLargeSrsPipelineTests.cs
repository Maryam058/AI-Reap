using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Infrastructure.Ai;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeHandler = AiReap.Tests.AnthropicAiChatClientTests.FakeHandler;

namespace AiReap.Tests;

// The Claude-only chain exactly as AddInfrastructure builds it (lossless request budget -> resilience ->
// AnthropicAiChatClient), over a fake HTTP transport, under the REAL pipeline services over
// InMemoryAiReapDb. Offline: no SQL Server (InMemoryAiReapDb), no network (every request is answered by
// FakeHandler and recorded; the tests assert on what was recorded), no AI provider.
public class ClaudeLargeSrsPipelineTests
{
    private const string Model = "claude-sonnet-5-5";

    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    internal sealed class ClaudeStack : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public ClaudeStack(FakeHandler handler, IDictionary<string, string?>? extra = null)
        {
            Handler = handler;
            var settings = new Dictionary<string, string?>
            {
                // No real backoff waits in tests.
                ["Ai:Resilience:BaseDelayMilliseconds"] = "1",
                ["Ai:Resilience:MaxDelaySeconds"] = "0",
            };
            foreach (var (key, value) in extra ?? new Dictionary<string, string?>()) settings[key] = value;

            // Gemini and Groq keys are present on purpose: they must never be used for chat.
            (_provider, _) = AiChatClientSelectionTests.BuildProvider(
                AiChatClientSelectionTests.PlausibleRealKey, Environments.Production,
                geminiKey: "AIzaSyAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", groqKey: "gsk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                extra: settings, anthropicModel: null,
                configure: s => s.AddHttpClient<AnthropicAiChatClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
            _scope = _provider.CreateScope();
            Client = _scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        }

        public FakeHandler Handler { get; }
        public IAiChatClient Client { get; }
        public RequestBudgetAiChatClient Budget => Assert.IsType<RequestBudgetAiChatClient>(Client);

        public List<JsonObject> MessageRequests => Handler.Requests
            .Where(r => r.Request.RequestUri!.AbsolutePath == "/v1/messages")
            .Select(r => JsonNode.Parse(r.Body)!.AsObject()).ToList();

        public List<string> MessageUserPrompts => MessageRequests.Select(UserPrompt).ToList();

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _provider.DisposeAsync();
        }
    }

    // The lossless clean-up the budget applies (trailing spaces, runs of spaces/blank lines) changes only
    // whitespace; content comparisons normalize it.
    private static string Ws(string text) => Regex.Replace(Regex.Replace(text, @"[ \t]+", " "), @"\s*\n\s*", "\n").Trim();

    internal static string UserPrompt(JsonObject request) => request["messages"]![0]!["content"]!.GetValue<string>();
    internal static string SystemPrompt(JsonObject request) => request["system"]?.GetValue<string>() ?? string.Empty;

    internal static HttpResponseMessage Ok(string text, string stopReason = "end_turn") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { model = Model, content = new[] { new { type = "text", text } }, stop_reason = stopReason })
    };

    internal static FakeHandler Answering(Func<JsonObject, HttpResponseMessage> messages, Func<JsonObject, HttpResponseMessage>? countTokens = null) =>
        new((request, body) =>
        {
            var json = JsonNode.Parse(body)!.AsObject();
            return request.RequestUri!.AbsolutePath == "/v1/messages/count_tokens"
                ? (countTokens ?? (_ => throw new InvalidOperationException("count_tokens was not expected")))(json)
                : messages(json);
        });

    private static string Source => GoldenFixture.Source(GoldenFixture.ClinicBooking);

    // Comparable to the source that produced the "~84,308 estimated input tokens" refusal observed with
    // Groq: the clinic SRS with enough unique filler sections to reach that size.
    private static string LargeSource => GoldenFixture.Large(Source, sections: 290, linesPerSection: 10);

    private static readonly string AnalysisResponse = new JsonObject
    {
        ["actors"] = new JsonArray("Receptionist", "Clinician"),
        ["capabilities"] = new JsonArray("Book consultation rooms"),
        ["dataElements"] = new JsonArray("Booking"),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(new JsonObject
        {
            ["topic"] = "Cancellation", ["question"] = "How late may a booking be cancelled?", ["reason"] = "The SRS does not say."
        })
    }.ToJsonString();

    internal static (InMemoryAiReapDb Db, RequirementSource Source) Seed(string rawText)
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "Claude offline project", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = rawText,
            CreatedByUserId = "phase0-offline-user"
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    // A later stage builds on the source's FRs and is refused without one (release rule), so the
    // later-stage tests seed one approved FR, as a real later stage would have.
    private static void WithFunctionalRequirement(InMemoryAiReapDb db, RequirementSource source) => db.Artifacts.Add(new Artifact
    {
        Id = Guid.NewGuid(), ProjectId = source.ProjectId, RequirementSourceId = source.Id, ArtifactType = ArtifactType.FunctionalRequirement,
        Code = "FR-001", Title = "Book a room", Status = ArtifactStatus.Approved, Origin = ArtifactOrigin.Ai, DataJson = "{}", CurrentVersion = 1,
        CreatedByUserId = "phase0-offline-user", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });

    private static RequirementAnalysisService Analysis(InMemoryAiReapDb db, IAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementAnalysisService>.Instance);

    internal static RequirementGenerationService Generation(InMemoryAiReapDb db, IAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementGenerationService>.Instance);

    private static BusinessRuleService BusinessRules(InMemoryAiReapDb db, IAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), NullLogger<BusinessRuleService>.Instance);

    private static IEnumerable<string> SrsIds(string text) =>
        Regex.Matches(text, @"\b(?:N?FR|BR|UC)-\d{3}\b").Select(m => m.Value).Distinct();

    // ---- the observed large SRS goes whole -----------------------------------------------------

    [Fact]
    public async Task An_srs_of_the_observed_size_is_analysed_in_one_whole_request_with_its_text_unchanged()
    {
        var large = LargeSource;
        var estimate = PromptBudgetReducer.EstimateRequestTokens("", large);
        Assert.InRange(estimate, 80_000, 100_000); // the size class of the observed ~84,308-token request

        await using var claude = new ClaudeStack(Answering(_ => Ok(AnalysisResponse)));
        var (db, source) = Seed(large);

        var result = await Analysis(db, claude.Client).AnalyzeAsync(source.Id);

        var request = Assert.Single(claude.MessageRequests);
        Assert.Contains(Ws(large), Ws(UserPrompt(request)));       // the whole source, whitespace aside
        Assert.Equal(Model, request["model"]!.GetValue<string>());
        Assert.Equal(RequirementCompleteness.NoReduction, result.Completeness!.ContextReduction);
        Assert.Equal(1, result.Completeness.PartCount);
        Assert.Equal([Model], result.Completeness.Models);
        // audit attribution (the analysis record next to it is deterministic, not a model call)
        Assert.Equal(Model, Assert.Single(InMemoryAiReapDb.Rows(db.AIExecutions), e => e.OperationType == "RequirementAnalysis").Model);
        Assert.Equal(AnalysisCoverageReport.Complete, result.Coverage!.Status);
    }

    // ---- over the application budget: lossless split with coverage ------------------------------

    [Fact]
    public async Task A_source_over_the_budget_is_split_into_parts_that_together_carry_every_line_and_id()
    {
        var large = LargeSource;
        // A deliberately small application budget so the same source must be split.
        var settings = new Dictionary<string, string?> { ["Ai:Anthropic:MaxRequestTokens"] = "56000" };
        await using var claude = new ClaudeStack(Answering(_ => Ok(AnalysisResponse)), settings);
        var (db, source) = Seed(large);

        var result = await Analysis(db, claude.Client).AnalyzeAsync(source.Id);

        var requests = claude.MessageRequests;
        Assert.True(requests.Count > 1, $"expected a split, got {requests.Count} request(s)");
        Assert.InRange(requests.Count, 2, claude.Budget.MaxChunkedParts);
        Assert.Equal(requests.Count, result.Completeness!.PartCount);
        Assert.Equal(RequirementCompleteness.Split, result.Completeness.ContextReduction);

        // The analysis itself reports every section as sent, and which part carried each.
        var coverage = result.Coverage!;
        Assert.Equal(AnalysisCoverageReport.Complete, coverage.Status);
        Assert.Equal("split", coverage.Method);
        Assert.True(coverage.SectionCount > 290, $"expected the filler sections to be counted, got {coverage.SectionCount}");
        Assert.Equal(coverage.SectionCount, coverage.SectionsCovered);
        Assert.Empty(coverage.MissingSections);
        Assert.Equal(coverage.SectionCount, coverage.Parts.Sum(p => p.Sections.Count));
        Assert.Empty(result.StaleReasons!);

        // Every part fits the budget it was planned for (estimated the way the budget is enforced).
        Assert.All(requests, r => Assert.True(
            PromptBudgetReducer.EstimateRequestTokens(SystemPrompt(r), UserPrompt(r)) <= claude.Budget.InputBudget));

        // Coverage: no line and no requirement ID of the source is missing from what was sent. (The
        // lossless clean-up may collapse runs of spaces, so whitespace is normalized on both sides.)
        static string Spaces(string s) => Regex.Replace(s, @"[ \t]+", " ").Trim();
        var sent = Spaces(string.Join("\n", requests.Select(UserPrompt)));
        var missingLines = large.Split('\n').Select(Spaces).Where(l => l.Length > 0 && !sent.Contains(l)).ToList();
        Assert.Empty(missingLines);
        Assert.All(SrsIds(large), id => Assert.Contains(id, sent));
        Assert.All(requests, r => Assert.Equal(Model, r["model"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_source_needing_more_parts_than_allowed_is_refused_before_anything_is_sent_or_saved()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Ai:Anthropic:MaxRequestTokens"] = "56000", ["Ai:Anthropic:MaxChunkedParts"] = "2"
        };
        await using var claude = new ClaudeStack(Answering(_ => Ok(AnalysisResponse)), settings);
        var (db, source) = Seed(LargeSource);

        var ex = await Assert.ThrowsAsync<AiChunkLimitExceededException>(() => Analysis(db, claude.Client).AnalyzeAsync(source.Id));

        Assert.Equal("Anthropic", ex.Provider);
        Assert.Equal("Ai:Anthropic:MaxChunkedParts", ex.MaxPartsSetting);
        Assert.Contains("Nothing was sent and nothing was saved", ex.Message);
        Assert.Empty(claude.Handler.Requests);   // proven: no HTTP request at all
        Assert.Equal(0, db.SaveCount);           // proven: SaveChangesAsync never called
    }

    // ---- later stages: the full source, whole or not at all --------------------------------------

    [Fact]
    public async Task A_later_stage_receives_the_complete_source_text_not_only_an_analysis_summary()
    {
        var large = LargeSource;
        var response = new JsonObject
        {
            ["businessRules"] = new JsonArray(new JsonObject
            {
                ["title"] = "Booking window", ["statement"] = "Rooms can be booked only within the stated window.",
                ["relatedRequirementCodes"] = new JsonArray()
            })
        }.ToJsonString();
        await using var claude = new ClaudeStack(Answering(_ => Ok(response)));
        var (db, source) = Seed(large);
        WithFunctionalRequirement(db, source);

        await BusinessRules(db, claude.Client).GenerateAsync(source.Id);

        var prompt = UserPrompt(Assert.Single(claude.MessageRequests));
        Assert.StartsWith(Ws(large), Ws(prompt));
        Assert.Equal(Model, Assert.Single(InMemoryAiReapDb.Rows(db.AIExecutions)).Model);
    }

    [Fact]
    public async Task A_later_stage_over_the_budget_is_refused_whole_without_sending_or_saving()
    {
        var settings = new Dictionary<string, string?> { ["Ai:Anthropic:MaxRequestTokens"] = "56000" };
        await using var claude = new ClaudeStack(Answering(_ => Ok("{\"businessRules\":[]}")), settings);
        var (db, source) = Seed(LargeSource);
        WithFunctionalRequirement(db, source);

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => BusinessRules(db, claude.Client).GenerateAsync(source.Id));

        Assert.Equal("Anthropic", ex.Provider);
        Assert.False(ex.RepairAttempt);
        Assert.Contains("was not sent", ex.Message);
        Assert.Empty(claude.Handler.Requests);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task A_plain_call_over_the_budget_is_refused_rather_than_condensed()
    {
        var settings = new Dictionary<string, string?> { ["Ai:Anthropic:MaxRequestTokens"] = "56000" };
        await using var claude = new ClaudeStack(Answering(_ => Ok("{}")), settings);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => claude.Client.CompleteAsync("system", LargeSource));

        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("Nothing was sent", ex.Message);
        Assert.Contains("Ai:Anthropic:MaxRequestTokens", ex.Message);
        Assert.Empty(claude.Handler.Requests);
    }

    // ---- output truncation and malformed output ----------------------------------------------------

    [Fact]
    public async Task A_generation_cut_off_at_max_tokens_is_rejected_after_one_request_and_nothing_is_saved()
    {
        await using var claude = new ClaudeStack(Answering(_ => Ok("{\"functionalRequirements\": [{\"title\": \"Book a ro", "max_tokens")));
        var (db, source) = Seed(Source);

        // The small source is sent whole; cut off, it is too small to split further, so the run fails.
        var ex = await Assert.ThrowsAsync<AiOutputTruncatedException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        Assert.Contains("did not fit the output limit", ex.Message);
        Assert.Contains(ex.Errors, e => e.Contains("truncated"));
        Assert.Contains("at least 1 AI request(s) were sent", ex.Message);
        Assert.Single(claude.MessageRequests); // no repair: the same limit would cut it off again
        Assert.Equal(0, db.SaveCount);
        Assert.Empty(InMemoryAiReapDb.Rows(db.Artifacts));
    }

    [Fact]
    public async Task Malformed_json_gets_one_repair_attempt_and_is_then_rejected_with_nothing_saved()
    {
        await using var claude = new ClaudeStack(Answering(_ => Ok("Here are the requirements: FR-001 ...")));
        var (db, source) = Seed(Source);

        await Assert.ThrowsAsync<AiOutputValidationException>(() => Generation(db, claude.Client).GenerateAsync(source.Id));

        var prompts = claude.MessageUserPrompts;
        Assert.Equal(2, prompts.Count);
        Assert.Contains("rejected by automatic validation", prompts[1]);
        Assert.Equal(0, db.SaveCount);
    }

    // ---- traceability and invented numbers ---------------------------------------------------------

    [Fact]
    public async Task Claude_output_goes_through_the_same_evidence_checks_and_an_unsupported_numeric_target_is_flagged()
    {
        var perfect = GoldenFixture.Candidate("generation-perfect");
        var fr = perfect.Requirements.First(r => r.Kind == "FR");
        var response = new JsonObject
        {
            ["functionalRequirements"] = new JsonArray(new JsonObject
            {
                ["title"] = fr.Title, ["actor"] = fr.Actor, ["priority"] = "High",
                ["preconditions"] = null, ["inputs"] = null, ["processing"] = null, ["expectedResult"] = fr.Text,
                ["dependencies"] = new JsonArray(), ["businessObjectiveCodes"] = new JsonArray(), ["sourceReferences"] = new JsonArray(),
                ["sourceIds"] = new JsonArray(fr.SourceIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["dependsOnSourceIds"] = new JsonArray(), ["requirementOrigin"] = fr.Origin, ["derivationNote"] = null,
                ["assumedClarifications"] = new JsonArray(),
                ["evidence"] = new JsonArray(fr.Evidence.Select(q => (JsonNode)new JsonObject
                    { ["from"] = q.From, ["quote"] = q.Quote, ["sourceId"] = q.SourceId, ["ref"] = q.Ref }).ToArray())
            }),
            ["nonFunctionalRequirements"] = new JsonArray(new JsonObject
            {
                // A numeric target the SRS never states, with no quote: must never pass as supported.
                ["title"] = "Availability", ["category"] = "Availability", ["description"] = "The system is highly available.",
                ["targetValue"] = "99.999% monthly uptime", ["targetOrigin"] = "stated", ["sourceIds"] = new JsonArray(),
                ["requirementOrigin"] = "explicit", ["derivationNote"] = null, ["assumedClarifications"] = new JsonArray(),
                ["evidence"] = new JsonArray()
            })
        }.ToJsonString();
        await using var claude = new ClaudeStack(Answering(_ => Ok(response)));
        var (db, source) = Seed(Source);

        await Generation(db, claude.Client).GenerateAsync(source.Id);

        Assert.Contains(Ws(Source), Ws(Assert.Single(claude.MessageUserPrompts)));
        var nfr = InMemoryAiReapDb.Rows(db.Artifacts).Single(a => a.ArtifactType == ArtifactType.NonFunctionalRequirement);
        var payload = JsonSerializer.Deserialize<NonFunctionalRequirementPayload>(nfr.DataJson, Lenient)!;
        Assert.Equal("not_supported", payload.TargetSupport);
        Assert.Equal("proposed_assumption", payload.AssumptionStatus);
        var frPayload = JsonSerializer.Deserialize<FunctionalRequirementPayload>(
            InMemoryAiReapDb.Rows(db.Artifacts).Single(a => a.ArtifactType == ArtifactType.FunctionalRequirement).DataJson, Lenient)!;
        Assert.Equal("verified", frPayload.EvidenceStatus); // quote found in the original SRS
        Assert.All(InMemoryAiReapDb.Rows(db.AIExecutions), e => Assert.Equal(Model, e.Model));
    }

    // ---- no fallback --------------------------------------------------------------------------

    [Theory]
    [InlineData(401, 1)]   // authentication: never retried, never another provider
    [InlineData(403, 1)]
    [InlineData(400, 1)]   // e.g. billing / credit balance
    [InlineData(500, 3)]   // transient: retried on Claude only (Ai:Resilience:MaxAttempts)
    [InlineData(529, 3)]
    [InlineData(429, 2)]   // rate limit: Ai:Resilience:MaxRateLimitAttempts, then the error - no fallback
    public async Task Claude_failures_surface_as_anthropic_errors_and_are_never_routed_to_gemini_or_groq(int status, int expectedRequests)
    {
        var handler = Answering(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = JsonContent.Create(new { type = "error", error = new { type = "some_error", message = "Your credit balance is too low" } })
            };
            if (status == 429) response.Headers.Add("retry-after", "0");
            return response;
        });
        await using var claude = new ClaudeStack(handler);
        var (db, source) = Seed(Source);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Analysis(db, claude.Client).AnalyzeAsync(source.Id));

        Assert.Equal("Anthropic", ex.Provider);
        Assert.Equal(expectedRequests, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("api.anthropic.com", r.Request.RequestUri!.Host));
        Assert.Equal(0, db.SaveCount);
    }

    // ---- opt-in token counting -----------------------------------------------------------------

    private static readonly Dictionary<string, string?> CountingSettings = new()
    {
        // Input budget 40,000 Claude tokens; the offline estimate of LargeSource is far above its
        // scaled equivalent, so only a precise count can let it through whole.
        ["Ai:Anthropic:MaxRequestTokens"] = "56000",
        ["Ai:Anthropic:UseTokenCountingApi"] = "true"
    };

    [Fact]
    public async Task With_token_counting_a_request_the_estimate_rejects_but_the_count_accepts_is_sent_whole()
    {
        var handler = Answering(_ => Ok("{}"), countTokens: _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { input_tokens = 39_000 })
        });
        await using var claude = new ClaudeStack(handler, CountingSettings);
        var large = LargeSource;

        await claude.Client.CompleteUncondensedAsync("system", large);

        Assert.Equal(1, handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath == "/v1/messages/count_tokens"));
        Assert.Equal(large, Assert.Single(claude.MessageUserPrompts)); // unchanged, not even cleaned up
    }

    [Fact]
    public async Task With_token_counting_a_request_the_count_also_rejects_is_refused_for_splitting_without_sending()
    {
        var handler = Answering(_ => Ok("{}"), countTokens: _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { input_tokens = 120_000 })
        });
        await using var claude = new ClaudeStack(handler, CountingSettings);

        await Assert.ThrowsAsync<AiRequestOverBudgetException>(() => claude.Client.CompleteUncondensedAsync("system", LargeSource));

        Assert.Empty(claude.MessageRequests);
    }

    [Fact]
    public async Task A_failed_token_count_falls_back_to_the_offline_estimate_and_never_sends_an_over_budget_request()
    {
        var handler = Answering(_ => Ok("{}"), countTokens: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = JsonContent.Create(new { type = "error", error = new { type = "api_error", message = "boom" } })
        });
        await using var claude = new ClaudeStack(handler, CountingSettings);

        await Assert.ThrowsAsync<AiRequestOverBudgetException>(() => claude.Client.CompleteUncondensedAsync("system", LargeSource));

        Assert.Empty(claude.MessageRequests);
    }

    // ---- dry-run plan agrees with the real call --------------------------------------------------

    [Fact]
    public async Task The_dry_run_route_reports_the_limits_the_claude_chain_enforces()
    {
        await using var claude = new ClaudeStack(Answering(_ => Ok("{}")));

        var route = AiRouteInspector.Inspect(claude.Client);

        var primary = route.Primary!;
        Assert.Null(route.Fallback);
        Assert.Equal("Anthropic", primary.Provider);
        Assert.Equal(Model, primary.Model);
        Assert.Equal(claude.Budget.InputBudget, primary.InputBudget);
        Assert.Equal(16000, primary.MaxOutputTokens);
        Assert.Equal("Ai:Anthropic:MaxTokens", primary.MaxOutputTokensSetting);
        Assert.Equal(600, primary.TimeoutSeconds);
        Assert.Equal(ChunkPlanOutcome.Whole,
            ChunkPlanner.Plan("system", LargeSource, "", GenerationSupport.SharedContext.None, primary.InputBudget, primary.MaxParts).Outcome);
    }
}
