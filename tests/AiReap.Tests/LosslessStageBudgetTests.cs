using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Agents;
using AiReap.Application.Agents.Implementations;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Common;
using AiReap.Application.Knowledge;
using AiReap.Application.Lifecycle;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AiReap.Tests.AgentStatus;
using static AiReap.Tests.GroundedGenerationTests;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// P0-5 Phase 2 — the ten later pipeline stages (user stories, acceptance criteria, business rules,
// quality review, conflict detection, solution design, data entities, API specifications,
// implementation tasks, test cases) send their prompt whole or not at all: a request over the input
// budget of the provider serving it is refused before it is sent (502 ai_request_rejected with the
// stage and token figures), never condensed. Each stage runs through its real service and endpoint;
// its AI call goes through the production Gemini -> Groq stack (FallbackAiChatClient, cool-down,
// ResilientAiChatClient, RequestBudgetAiChatClient) over fake HTTP - never a live provider. Analyze
// and Generate keep their own lossless-or-split path (ChunkedAnalysisTests,
// ChunkedRequirementGenerationTests) and are scripted here only to create the synthetic FRs.
public class LosslessStageBudgetTests
{
    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";

    // appsettings.json Ai:Groq: MaxRequestTokens 8000, ReservedOutputTokens 4000, MaxChunkedParts 8.
    private const int MaxRequestTokens = 8000;
    private const int ReservedOutputTokens = 4000;
    private const int InputBudget = MaxRequestTokens - ReservedOutputTokens;

    // PromptBudgetReducer's omission marker, present in any condensed prompt.
    private const string OmissionText = "omitted to fit the AI provider's request budget";

    // Sentinel in human-edited oversized fields, so a leak into an error response is detectable.
    private const string EditSentinel = "EDITSENTINEL";

    // The stage of a forwarded call by system-prompt keyword (StubAiChatClient's order), named by the
    // operationType the service passes - the identifier the refusal reports.
    private static readonly (string Keyword, string Stage)[] StageKeywords =
    [
        ("userStories", "UserStoryGeneration"), ("acceptanceCriteria", "AcceptanceCriteriaGeneration"),
        ("businessRules", "BusinessRuleExtraction"), ("relationshipType", "ConflictDetection"),
        ("artifactCode", "RequirementQualityAnalysis"), ("architectureOverview", "SolutionDesignGeneration"),
        ("dataEntities", "DatabaseDesignGeneration"), ("apiSpecifications", "ApiDesignGeneration"),
        ("taskType", "ImplementationPlanning"), ("testCases", "TestCaseGeneration")
    ];

    private static string StageOf(string systemPrompt) =>
        StageKeywords.FirstOrDefault(s => systemPrompt.Contains(s.Keyword)).Stage ?? "unknown";

    // The stub's answer to the prompt actually sent. The user prompt matters: the stub cites the first
    // FR listed under FunctionalRequirementsHeading, and with no list it cites nothing (which the
    // stages' CodesRequired validation rejects when FRs were provided).
    private static string StubAnswer(string systemPrompt, string userPrompt) =>
        new StubAiChatClient().CompleteAsync(systemPrompt, userPrompt).GetAwaiter().GetResult();

    // ---- providers: the production stack over fake HTTP ------------------------------------

    private sealed class Providers
    {
        public GroqAiChatClientTests.FakeHandler GeminiHttp { get; }
        public GroqAiChatClientTests.FakeHandler GroqHttp { get; }
        public AiProviderCooldown Cooldown { get; } = new();
        public IAiChatClient Client { get; }

        // Gemini answers 429 "quota exceeded (per day)" instead of the stub answer.
        public bool GeminiQuota { get; set; }

        // Gemini answers 503 UNAVAILABLE (an outage: falls back, records no cool-down).
        public bool GeminiOutage { get; set; }

        // Groq's answer to a request, by its system and user prompts.
        public Func<string, string, string> GroqContent { get; set; } = StubAnswer;

        // Wired as DependencyInjection wires it: Gemini resilient without 429 retry; Groq resilient
        // inside the request budget; both inside FallbackAiChatClient. groqOnly: Groq is the only
        // provider (Ai:Provider=Groq without a Gemini key) - the budget client with no fallback.
        // maxRequestTokens: Ai:Groq:MaxRequestTokens (the output reserve stays ReservedOutputTokens).
        // groqPrimary: Ai:Provider=Groq with Gemini as its fallback.
        public Providers(bool groqOnly = false, int maxRequestTokens = MaxRequestTokens, bool groqPrimary = false)
        {
            GeminiHttp = new(_ => GeminiOutage
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent.Create(new { error = new { code = 503, message = "The model is overloaded. Please try again later.", status = "UNAVAILABLE" } })
                }
                : GeminiQuota
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    // Gemini's free-tier daily quota (FallbackAiChatClientTests' shape).
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
                }
                : GroqAiChatClientTests.Json(new
                {
                    candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = GeminiStubAnswer(GeminiHttp!.Bodies[^1]) } } }, finishReason = "STOP" } }
                }));
            GroqHttp = new(_ =>
            {
                var (system, user) = GroqMessages(GroqHttp!.Bodies[^1]);
                return GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(GroqContent(system, user)));
            });

            var gemini = new GeminiAiChatClient(new HttpClient(GeminiHttp), Options.Create(new GeminiOptions { ApiKey = GeminiKey }));
            var groq = new GroqAiChatClient(new HttpClient(GroqHttp), Options.Create(new GroqOptions { ApiKey = GroqKey, MaxRequestTokens = maxRequestTokens }));
            var resilience = new AiResilienceOptions { BaseDelayMilliseconds = 0 };
            Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

            var budgetedGroq = new RequestBudgetAiChatClient(
                new ResilientAiChatClient(groq, "Groq", resilience, NullLogger.Instance, NoDelay), "Groq",
                "Ai:Groq:MaxRequestTokens", maxRequestTokens, ReservedOutputTokens, NullLogger.Instance,
                8, "Ai:Groq:MaxChunkedParts");
            Client = groqOnly
                ? budgetedGroq
                : groqPrimary
                    ? new FallbackAiChatClient(
                        budgetedGroq, "Groq",
                        new ResilientAiChatClient(gemini, "Gemini", resilience, NullLogger.Instance, NoDelay), "Gemini",
                        Cooldown, TimeSpan.FromMinutes(10), NullLogger.Instance)
                    : new FallbackAiChatClient(
                        new ResilientAiChatClient(gemini, "Gemini", resilience.WithoutRateLimitRetry(), NullLogger.Instance, NoDelay), "Gemini",
                        budgetedGroq, "Groq", Cooldown, TimeSpan.FromMinutes(10), NullLogger.Instance);
        }

        // Gemini's daily quota used up earlier: every call goes straight to Groq.
        public void CoolDownGemini() => Cooldown.CoolDown("Gemini", DateTimeOffset.UtcNow.AddMinutes(10));
    }

    private static (string System, string User) GroqMessages(string body)
    {
        var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
        return (messages[0]!["content"]!.GetValue<string>(), messages[1]!["content"]!.GetValue<string>());
    }

    private static string GeminiStubAnswer(string body)
    {
        var (system, user) = GeminiMessages(body);
        return StubAnswer(system, user);
    }

    private static (string System, string User) GeminiMessages(string body)
    {
        var json = JsonNode.Parse(body)!;
        var system = (json["systemInstruction"] ?? json["system_instruction"])!["parts"]![0]!["text"]!.GetValue<string>();
        return (system, json["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    // ---- the AI client the API uses ---------------------------------------------------------

    public sealed record ForwardedCall(string Stage, string System, string User, bool Uncondensed);

    // Analyze and Generate are scripted; every later-stage call is recorded and forwarded to the
    // provider stack exactly as the service made it (CompleteAsync or CompleteUncondensedAsync).
    // Forward = false answers everything with the stub - setup only, never under test.
    private sealed class Router(IAiChatClient providers, Func<string> generation) : IUncondensedAiChatClient
    {
        private readonly StubAiChatClient _stub = new();
        private IAiChatClient _served = providers;

        public bool Forward { get; set; } = true;
        public List<ForwardedCall> Calls { get; } = new();
        public string ModelName => _served.ModelName;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            Send(systemPrompt, userPrompt, uncondensed: false, cancellationToken);

        public Task<string> CompleteUncondensedAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            Send(systemPrompt, userPrompt, uncondensed: true, cancellationToken);

        private Task<string> Send(string systemPrompt, string userPrompt, bool uncondensed, CancellationToken cancellationToken)
        {
            if (systemPrompt.Contains("requirementOrigin"))
            {
                _served = _stub;
                return Task.FromResult(generation());
            }

            if (!Forward || systemPrompt.Contains("missingInformation"))
            {
                _served = _stub;
                return _stub.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
            }

            _served = providers;
            lock (Calls) Calls.Add(new ForwardedCall(StageOf(systemPrompt), systemPrompt, userPrompt, uncondensed));
            return uncondensed
                ? providers.CompleteUncondensedAsync(systemPrompt, userPrompt, cancellationToken)
                : providers.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }
    }

    // One FR per requirement ID (the first frCount), each citing it, plus three NFRs - the synthetic
    // set ContextReductionMeasurementTests measured.
    private static string SyntheticGeneration(string source, int frCount)
    {
        var ids = Regex.Matches(source, @"REQ-(\d+)\.(\d+)").Select(m => (Id: m.Value, Area: m.Groups[1].Value, Rule: m.Groups[2].Value))
            .DistinctBy(i => i.Id).Take(frCount).ToList();
        var frs = new JsonArray(ids.Select(i =>
        {
            var fr = FrItem($"Record leave for area {i.Area} rule {i.Rule}", "explicit",
                new JsonArray(Quote("srs", $"The system must record every leave request for area {i.Area} with its dates", i.Id)),
                expectedResult: $"The leave request for area {i.Area} is stored with its dates, type and approver and kept for seven years.");
            fr["preconditions"] = $"The employee is signed in and has a leave balance for area {i.Area}.";
            fr["inputs"] = "Leave type, start date, end date, approver";
            fr["processing"] = $"Validate the dates against the area {i.Area} calendar, then record the request.";
            fr["sourceIds"] = new JsonArray(i.Id);
            return (JsonNode)fr;
        }).ToArray());
        var nfrs = new JsonArray(
            NfrItem("Leave records retained seven years", null, null, new JsonArray(Quote("srs", "and keep it for seven years"))),
            NfrItem("Leave page responsive", "2 seconds", "proposed", null),
            NfrItem("Audit history available", null, null, null));
        return Response(frs, nfrs);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required Providers Providers { get; init; }
        public required Router Ai { get; init; }
        public required TestHost Host { get; init; }
        public required ApiUser Ba { get; init; }
        public required string ProjectId { get; init; }
        public required string SourceId { get; init; }
        public required string Source { get; init; }
        public JsonArray Frs { get; private set; } = new();

        // A synthetic source of ~sourceTokens request tokens; analyzed and given frCount FRs unless
        // generate is false (an agent run does both itself).
        public static async Task<Fixture> CreateAsync(Providers providers, int sourceTokens, int frCount, bool generate = true)
        {
            var source = ChunkedAnalysisTests.StructuredSource(sourceTokens);
            var ai = new Router(providers.Client, () => SyntheticGeneration(source, frCount));
            var host = new TestHost(chatClient: ai);
            var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
            var (projectId, sourceId) = await SourceAsync(ba, source);
            var fixture = new Fixture
            {
                Providers = providers, Ai = ai, Host = host, Ba = ba, ProjectId = projectId, SourceId = sourceId, Source = source
            };
            if (generate)
            {
                await fixture.RunAsync($"/api/requirement-sources/{sourceId}/analyze");
                var generated = await fixture.RunAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
                fixture.Frs = generated!["functionalRequirements"]!.AsArray();
                Assert.Equal(frCount, fixture.Frs.Count);
            }

            return fixture;
        }

        public async Task<JsonNode?> RunAsync(string path)
        {
            var (status, body) = await Ba.PostAsync(path);
            Assert.True(status == 200, $"{path} returned {status}: {body?.ToJsonString()}");
            return body;
        }

        // A human edit (PATCH /api/artifacts/{id}) replacing one field of the artifact's data.
        public async Task EditAsync(string artifactId, string field, string value)
        {
            var (getStatus, artifact) = await Ba.GetAsync($"/api/artifacts/{artifactId}");
            Assert.Equal(200, getStatus);
            var data = artifact!["data"]!.AsObject();
            data[field] = value;
            var (patchStatus, body) = await Ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{artifactId}", new { data, reason = "Expanded" });
            Assert.True(patchStatus == 200, $"edit returned {patchStatus}: {body?.ToJsonString()}");
        }

        public Task<int> CountAsync(string table) => Host.ScalarAsync($"SELECT COUNT(*) FROM {table}");

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    // The ten in-scope stages and the endpoint that runs each.
    private static (string Stage, string Path)[] StagePaths(Fixture f, string storyId, string frId) =>
    [
        ("UserStoryGeneration", $"/api/requirement-sources/{f.SourceId}/generate-user-stories"),
        ("AcceptanceCriteriaGeneration", $"/api/artifacts/{storyId}/generate-acceptance-criteria"),
        ("BusinessRuleExtraction", $"/api/requirement-sources/{f.SourceId}/generate-business-rules"),
        ("RequirementQualityAnalysis", $"/api/requirement-sources/{f.SourceId}/analyze-quality"),
        ("ConflictDetection", $"/api/projects/{f.ProjectId}/detect-conflicts"),
        ("SolutionDesignGeneration", $"/api/requirement-sources/{f.SourceId}/generate-design"),
        ("DatabaseDesignGeneration", $"/api/requirement-sources/{f.SourceId}/generate-data-entities"),
        ("ApiDesignGeneration", $"/api/requirement-sources/{f.SourceId}/generate-api-specs"),
        ("ImplementationPlanning", $"/api/requirement-sources/{f.SourceId}/generate-tasks"),
        ("TestCaseGeneration", $"/api/artifacts/{frId}/generate-test-cases")
    ];

    // ~6,000 tokens of distinct sentences (no duplicate lines or extra whitespace, so lossless clean-up
    // cannot shrink it), with the sentinel.
    private static string Oversized(string label) =>
        string.Join(" ", Enumerable.Range(0, 420).Select(i => $"{EditSentinel} {label} clause {Letters(i)} adds one more distinct detail."));

    private static string Letters(int n)
    {
        var s = "";
        do { s = (char)('a' + n % 26) + s; n /= 26; } while (n > 0);
        return "x" + s;
    }

    // Distinct lines whose text is estimated at exactly `tokens` tokens.
    private static string TextOfTokens(int tokens)
    {
        var chars = (int)(tokens * PromptBudgetReducer.CharsPerToken);
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < chars; i++)
        {
            sb.Append($"Line {Letters(i)} records one more distinct fact about the leave process.\n");
        }

        return sb.ToString(0, chars);
    }

    // ---- assertions --------------------------------------------------------------------------

    // Items 2 and 7: the refusal keeps every existing field, adds the diagnostics, and carries no
    // prompt or source text and no credential.
    private static void AssertRefusal(JsonNode body, string stage, ForwardedCall call, bool primaryNamed, bool repairAttempt = false)
    {
        Assert.Equal(502, body["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["title"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(body["instance"]?.GetValue<string>()));
        Assert.Equal("ai_request_rejected", body["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.Equal("Groq", body["provider"]!.GetValue<string>());
        Assert.False(body["sentToProvider"]!.GetValue<bool>());
        Assert.Equal(stage, body["stage"]!.GetValue<string>());

        var estimated = PromptBudgetReducer.EstimateRequestTokens(call.System, call.User);
        Assert.Equal(estimated, body["estimatedInputTokens"]!.GetValue<int>());
        Assert.Equal(InputBudget, body["inputBudget"]!.GetValue<int>());
        Assert.Equal(estimated - InputBudget, body["overBudgetTokens"]!.GetValue<int>());
        Assert.True(body["overBudgetTokens"]!.GetValue<int>() > 0);

        var detail = body["detail"]!.GetValue<string>();
        Assert.Contains("nothing was saved", detail);
        Assert.Contains("does not shorten its input", detail);
        if (repairAttempt)
        {
            Assert.Contains($"The first {stage} request was sent to Groq", detail);
            Assert.Contains("The repair attempt was not sent", detail);
        }
        else
        {
            Assert.Contains("No request was sent to Groq", detail);
        }

        if (primaryNamed)
        {
            Assert.Equal("Gemini", body["primaryProvider"]!.GetValue<string>());
            Assert.StartsWith("Gemini, the primary AI provider, did not serve this request", detail);
        }
        else
        {
            Assert.Null(body["primaryProvider"]);
        }

        AssertNoSensitiveText(body.ToJsonString(), call);
    }

    private static void AssertNoSensitiveText(string text, ForwardedCall call)
    {
        string[] forbidden =
        [
            GeminiKey, GroqKey, "Bearer", "Authorization", "x-goog-api-key", EditSentinel,
            // source sentences (ChunkedAnalysisTests.StructuredSource) and FR text the prompts carry
            "This section describes how leave area", "Employees see their balance", "Closing remarks",
            "The system must record every leave request", "Record leave for area",
            call.System[..Math.Min(80, call.System.Length)].Trim(), call.User[..Math.Min(80, call.User.Length)].Trim()
        ];
        Assert.All(forbidden, f => Assert.DoesNotContain(f, text, StringComparison.OrdinalIgnoreCase));
    }

    // ---- 1, 2, 6, 7: over budget -> refused before any provider call, nothing saved --------------

    [Fact]
    public async Task Every_in_scope_stage_over_the_groq_budget_is_refused_before_any_provider_call_and_saves_nothing()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        // The Phase 1 "medium" case (~8,000-token source, 25 FRs), which the condensing path shortened.
        await using var f = await Fixture.CreateAsync(providers, 8_000, 25);

        f.Ai.Forward = false;
        var story = (await f.RunAsync($"/api/requirement-sources/{f.SourceId}/generate-user-stories"))!.AsArray()[0]!;
        f.Ai.Forward = true;

        // Acceptance criteria and test cases carry one artifact; a human edit makes it oversized.
        await f.EditAsync(Id(story), "valueStatement", Oversized("story value"));
        await f.EditAsync(Id(f.Frs[0]), "expectedResult", Oversized("expected result"));

        var reached = new List<string>();
        foreach (var (stage, path) in StagePaths(f, Id(story), Id(f.Frs[0])))
        {
            var artifacts = await f.CountAsync("Artifacts");
            var versions = await f.CountAsync("ArtifactVersions");
            var executions = await f.CountAsync("AIExecutions");
            var before = f.Ai.Calls.Count;

            var (status, body) = await f.Ba.PostAsync(path);

            Assert.True(status == 502, $"{stage}: {status} {body?.ToJsonString()}");
            // Exactly one request made, uncondensed: no retry, no repair, no fallback loop.
            var call = Assert.Single(f.Ai.Calls.Skip(before));
            Assert.Equal(stage, call.Stage);
            Assert.True(call.Uncondensed, $"{stage} used the condensing path.");
            AssertRefusal(body!, stage, call, primaryNamed: true);

            Assert.Empty(providers.GroqHttp.Bodies);
            Assert.Empty(providers.GeminiHttp.Bodies);
            Assert.Equal(artifacts, await f.CountAsync("Artifacts"));
            Assert.Equal(versions, await f.CountAsync("ArtifactVersions"));
            Assert.Equal(executions, await f.CountAsync("AIExecutions"));
            reached.Add(stage);
        }

        Assert.Equal(StageKeywords.Select(s => s.Stage).Order(), reached.Order());
    }

    // ---- 3, 6: prompts that fit are sent exactly as built ----------------------------------------

    [Fact]
    public async Task Stages_that_fit_the_groq_budget_are_sent_exactly_as_built_with_nothing_omitted()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        // The Phase 1 "small" case (~2,500-token source, 10 FRs): every stage fits.
        await using var f = await Fixture.CreateAsync(providers, 2_500, 10);

        var stories = (await f.RunAsync($"/api/requirement-sources/{f.SourceId}/generate-user-stories"))!.AsArray();
        foreach (var (stage, path) in StagePaths(f, Id(stories[0]), Id(f.Frs[0])).Skip(1))
        {
            await f.RunAsync(path);
        }

        Assert.Equal(StageKeywords.Select(s => s.Stage).Order(), f.Ai.Calls.Select(c => c.Stage).Order());
        Assert.Empty(providers.GeminiHttp.Bodies);
        Assert.Equal(f.Ai.Calls.Count, providers.GroqHttp.Bodies.Count);
        for (var i = 0; i < f.Ai.Calls.Count; i++)
        {
            var call = f.Ai.Calls[i];
            var (system, user) = GroqMessages(providers.GroqHttp.Bodies[i]);
            Assert.True(call.Uncondensed, $"{call.Stage} used the condensing path.");
            Assert.Equal(call.System, system);
            Assert.Equal(call.User, user);
            Assert.DoesNotContain(OmissionText, user);
            Assert.True(PromptBudgetReducer.EstimateRequestTokens(system, user) <= InputBudget, call.Stage);
        }

        // Quality and conflict review carry every requirement block.
        var codes = f.Frs.Select(fr => fr!["code"]!.GetValue<string>()).ToList();
        foreach (var stage in new[] { "RequirementQualityAnalysis", "ConflictDetection" })
        {
            var user = f.Ai.Calls.Single(c => c.Stage == stage).User;
            Assert.All(codes, code => Assert.Contains($"{code}: ", user));
        }

        // Acceptance criteria and test cases: a normal-size story / FR goes out whole.
        Assert.Contains("Value: ", f.Ai.Calls.Single(c => c.Stage == "AcceptanceCriteriaGeneration").User);
        Assert.StartsWith($"{codes[0]}: ", f.Ai.Calls.Single(c => c.Stage == "TestCaseGeneration").User);
    }

    // ---- 5: fallback and cool-down -------------------------------------------------------------

    [Fact]
    public async Task Gemini_available_serves_an_over_groq_budget_stage_whole()
    {
        var providers = new Providers();
        await using var f = await Fixture.CreateAsync(providers, 8_000, 25);

        await f.RunAsync($"/api/requirement-sources/{f.SourceId}/generate-user-stories");

        var call = Assert.Single(f.Ai.Calls);
        Assert.True(PromptBudgetReducer.EstimateRequestTokens(call.System, call.User) > InputBudget);
        var (system, user) = GeminiMessages(Assert.Single(providers.GeminiHttp.Bodies));
        Assert.Equal(call.System, system);
        Assert.Equal(call.User, user);
        Assert.Empty(providers.GroqHttp.Bodies);
    }

    [Fact]
    public async Task Gemini_daily_quota_falls_back_to_groq_which_refuses_and_gemini_is_then_skipped_by_its_cooldown()
    {
        var providers = new Providers { GeminiQuota = true };
        await using var f = await Fixture.CreateAsync(providers, 8_000, 25);

        var (status, body) = await f.Ba.PostAsync($"/api/requirement-sources/{f.SourceId}/generate-user-stories");

        Assert.Equal(502, status);
        AssertRefusal(body!, "UserStoryGeneration", Assert.Single(f.Ai.Calls), primaryNamed: true);
        Assert.Equal("RateLimited", body!["primaryFailureKind"]!.GetValue<string>());
        Assert.True(body["primaryQuotaExhausted"]!.GetValue<bool>());
        Assert.Single(providers.GeminiHttp.Bodies); // no 429 retry on the primary
        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.True(providers.Cooldown.IsCoolingDown("Gemini", DateTimeOffset.UtcNow, out _));

        // The next stage skips Gemini (cool-down) and is refused by Groq's budget: still nothing sent.
        var (nextStatus, next) = await f.Ba.PostAsync($"/api/requirement-sources/{f.SourceId}/generate-business-rules");
        Assert.Equal(502, nextStatus);
        AssertRefusal(next!, "BusinessRuleExtraction", f.Ai.Calls[^1], primaryNamed: true);
        Assert.Null(next!["primaryFailureKind"]?.GetValue<string>());
        Assert.Single(providers.GeminiHttp.Bodies);
        Assert.Empty(providers.GroqHttp.Bodies);
    }

    [Fact]
    public async Task Groq_only_refusal_names_no_primary_and_repeating_the_request_sends_nothing()
    {
        var providers = new Providers(groqOnly: true);
        await using var f = await Fixture.CreateAsync(providers, 8_000, 25);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (status, body) = await f.Ba.PostAsync($"/api/requirement-sources/{f.SourceId}/generate-user-stories");
            Assert.Equal(502, status);
            Assert.Equal(attempt, f.Ai.Calls.Count);
            AssertRefusal(body!, "UserStoryGeneration", f.Ai.Calls[^1], primaryNamed: false);
        }

        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.Empty(providers.GeminiHttp.Bodies);
    }

    // ---- D1: the repair attempt, separately from the first request ------------------------------

    private sealed class Shape
    {
        public List<string>? Actors { get; set; }
    }

    private const string ShapeSystem = "Respond with ONLY a single JSON object of exactly this shape: {\"actors\": [\"string\"]}";

    private static string UserOfRequestTokens(int requestTokens) =>
        TextOfTokens(requestTokens - PromptBudgetReducer.RequestOverheadTokens - PromptBudgetReducer.EstimateTokens(ShapeSystem));

    [Fact]
    public async Task First_request_over_the_budget_is_refused_as_not_sent()
    {
        var providers = new Providers(groqOnly: true);
        var user = UserOfRequestTokens(InputBudget + 50);

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => GenerationSupport.CallAiAndParseLosslessAsync<Shape>(
            providers.Client, NullLogger.Instance, "ProbeStage", "probe", ShapeSystem, user, CancellationToken.None));

        Assert.False(ex.RepairAttempt);
        Assert.Equal("ProbeStage", ex.Stage);
        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Equal(InputBudget + 50, ex.EstimatedInputTokens);
        Assert.Equal(50, ex.OverBudgetTokens);
        Assert.Contains("so it was not sent", ex.Message);
        Assert.Empty(providers.GroqHttp.Bodies);
    }

    [Fact]
    public async Task Repair_attempt_over_the_budget_is_refused_before_it_is_sent_after_the_first_request_was_sent_whole()
    {
        var providers = new Providers(groqOnly: true) { GroqContent = (_, _) => "this is not JSON" };
        var user = UserOfRequestTokens(InputBudget - 30);

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => GenerationSupport.CallAiAndParseLosslessAsync<Shape>(
            providers.Client, NullLogger.Instance, "ProbeStage", "probe", ShapeSystem, user, CancellationToken.None));

        Assert.True(ex.RepairAttempt);
        Assert.True(ex.EstimatedInputTokens > InputBudget);
        Assert.Equal(ex.EstimatedInputTokens - InputBudget, ex.OverBudgetTokens);
        Assert.Contains("the first request was sent", ex.Message);
        Assert.Contains("so the repair attempt was not sent", ex.Message);
        Assert.DoesNotContain(user[..60], ex.Message);

        // Only the first request reached Groq, unchanged.
        var (system, sent) = GroqMessages(Assert.Single(providers.GroqHttp.Bodies));
        Assert.Equal(ShapeSystem, system);
        Assert.Equal(user, sent);
    }

    [Fact]
    public async Task Repair_attempt_that_fits_is_sent_whole_with_the_complete_first_prompt()
    {
        var answers = new Queue<string>(["this is not JSON", "{\"actors\":[\"Manager\"]}"]);
        var providers = new Providers(groqOnly: true) { GroqContent = (_, _) => answers.Dequeue() };
        var user = UserOfRequestTokens(1_500);

        var (_, parsed) = await GenerationSupport.CallAiAndParseLosslessAsync<Shape>(
            providers.Client, NullLogger.Instance, "ProbeStage", "probe", ShapeSystem, user, CancellationToken.None);

        Assert.Equal(["Manager"], parsed.Actors!);
        Assert.Equal(2, providers.GroqHttp.Bodies.Count);
        Assert.Equal(user, GroqMessages(providers.GroqHttp.Bodies[0]).User);
        var repair = GroqMessages(providers.GroqHttp.Bodies[1]).User;
        Assert.StartsWith(user, repair);
        Assert.Contains("Your previous response was rejected by automatic validation", repair);
        Assert.All(providers.GroqHttp.Bodies, b => Assert.DoesNotContain(OmissionText, b));
    }

    [Fact]
    public async Task Stage_repair_attempt_over_the_budget_is_reported_as_the_repair_not_sent()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        await using var f = await Fixture.CreateAsync(providers, 2_500, 10);
        f.Ai.Forward = false;
        var story = (await f.RunAsync($"/api/requirement-sources/{f.SourceId}/generate-user-stories"))!.AsArray()[0]!;
        f.Ai.Forward = true;

        // Learn the acceptance-criteria prompt, then size the story so the first request is just
        // within the budget and its repair (request + validation errors) is not. The value is not the
        // end of the prompt: the story's linked FRs follow it (AcceptanceCriteriaGeneration-v2), so
        // the room is what the whole prompt leaves around the (single-line) value.
        await f.RunAsync($"/api/artifacts/{Id(story)}/generate-acceptance-criteria");
        var probe = f.Ai.Calls[^1];
        var valueAt = probe.User.IndexOf("Value: ", StringComparison.Ordinal) + "Value: ".Length;
        var valueEnd = probe.User.IndexOf('\n', valueAt) is var newline and >= 0 ? newline : probe.User.Length;
        var (beforeValue, afterValue) = (probe.User[..valueAt], probe.User[valueEnd..]);
        var room = InputBudget - 30 - PromptBudgetReducer.EstimateRequestTokens(probe.System, beforeValue + afterValue);
        var value = TextOfTokens(room).Replace('\n', ' ').Trim();
        await f.EditAsync(Id(story), "valueStatement", value);

        // Precondition: the first request, as it will be built, fits the budget - so a refusal below can
        // only be the repair attempt's.
        Assert.True(PromptBudgetReducer.EstimateRequestTokens(probe.System, beforeValue + value + afterValue) <= InputBudget,
            "The sized first acceptance-criteria request does not fit the input budget.");
        providers.GroqContent = (system, user) => system.Contains("acceptanceCriteria") ? "this is not JSON" : StubAnswer(system, user);
        var groqBefore = providers.GroqHttp.Bodies.Count;
        var callsBefore = f.Ai.Calls.Count;
        var artifacts = await f.CountAsync("Artifacts");
        var executions = await f.CountAsync("AIExecutions");

        var (status, body) = await f.Ba.PostAsync($"/api/artifacts/{Id(story)}/generate-acceptance-criteria");

        Assert.Equal(502, status);
        var calls = f.Ai.Calls.Skip(callsBefore).ToList();
        Assert.Equal(2, calls.Count);
        Assert.True(PromptBudgetReducer.EstimateRequestTokens(calls[0].System, calls[0].User) <= InputBudget);
        Assert.StartsWith(calls[0].User, calls[1].User);
        AssertRefusal(body!, "AcceptanceCriteriaGeneration", calls[1], primaryNamed: true, repairAttempt: true);
        // The first request was sent whole; the repair was not sent at all.
        Assert.Equal(groqBefore + 1, providers.GroqHttp.Bodies.Count);
        Assert.Equal(calls[0].User, GroqMessages(providers.GroqHttp.Bodies[^1]).User);
        Assert.Equal(artifacts, await f.CountAsync("Artifacts"));
        Assert.Equal(executions, await f.CountAsync("AIExecutions"));
    }

    // ---- agent pipeline: a refused stage fails the run stage with the safe message ---------------

    [Fact]
    public async Task Agent_stage_refused_for_its_size_fails_with_a_safe_message_and_saves_no_business_rules()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        await using var f = await Fixture.CreateAsync(providers, 8_000, 25, generate: false);

        var (startStatus, run) = await f.Ba.PostAsync("/api/agent-runs", new { requirementSourceId = f.SourceId });
        Assert.Equal(200, startStatus);
        Assert.Equal(Awaiting, run!["stages"]![0]!["status"]!.GetValue<int>());

        // Approving Requirements runs the Analysis agent: FR generation, then business rules.
        var (decideStatus, failed) = await f.Ba.DecideAsync(Id(run), true);

        Assert.Equal(200, decideStatus);
        Assert.Equal(RunFailed, failed!["status"]!.GetValue<int>());
        Assert.Equal(Failed, failed["stages"]![1]!["status"]!.GetValue<int>());
        var error = failed["stages"]![1]!["error"]!.GetValue<string>();
        Assert.StartsWith("BusinessRuleExtraction: the request is too large to send whole", error);
        Assert.Contains("so it was not sent", error);
        var call = Assert.Single(f.Ai.Calls);
        Assert.Equal("BusinessRuleExtraction", call.Stage);
        AssertNoSensitiveText(error, call);
        Assert.Equal(1, await f.Host.ScalarAsync(
            "SELECT COUNT(*) FROM AgentStageRuns WHERE Status = 5 AND Error LIKE 'BusinessRuleExtraction: the request is too large%'"));

        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.Empty(providers.GeminiHttp.Bodies);
        Assert.Equal(0, await f.Host.ScalarAsync("SELECT COUNT(*) FROM Artifacts WHERE ArtifactType = 2"));
    }

    // ---- Project Copilot: context fitted to the serving provider's budget -----------------------
    //
    // These run CopilotService.AskModelAsync - the Copilot's model call - over the production provider
    // stack (fake HTTP), with synthetic context (CopilotContextFitterTests.Input). No database.

    private sealed class TestRoute(IAiChatClient client) : IAiRouteInspector
    {
        public AiRouteSnapshot Inspect() => AiRouteInspector.Inspect(client);
    }

    private const string CopilotQuestion = CopilotContextFitterTests.Question;

    private static Task<(string RawResponse, CopilotService.CopilotAiResponse Parsed, CopilotFit Fit)> AskCopilotAsync(
        Providers providers, CopilotPromptInput input) =>
        CopilotService.AskModelAsync(providers.Client, new TestRoute(providers.Client), NullLogger.Instance, input,
            input.Artifacts.Select(a => a.Code), "copilot-test", CancellationToken.None);

    private static string StandardCopilotPrompt(CopilotPromptInput input) =>
        CopilotContextFitter.StandardFit(CopilotService.SystemPrompt, input).UserPrompt;

    private static void AssertNoCopilotText(string text, CopilotPromptInput input)
    {
        Assert.DoesNotContain("QSENTINEL", text);
        Assert.DoesNotContain("carry-over", text);
        Assert.DoesNotContain(input.Facts[..Math.Min(40, input.Facts.Length)], text);
        Assert.DoesNotContain("rule1 clause", text);
    }

    [Fact]
    public async Task Copilot_served_by_gemini_sends_the_standard_prompt_whole_in_one_routed_call()
    {
        // Small project: complete context, nothing reduced.
        var small = new Providers();
        var smallInput = CopilotContextFitterTests.Input(artifacts: 8, contentChars: 300, versions: 3, totalVersions: 3, excerpts: 2);
        var (_, _, smallFit) = await AskCopilotAsync(small, smallInput);
        Assert.Equal(StandardCopilotPrompt(smallInput), GeminiMessages(Assert.Single(small.GeminiHttp.Bodies)).User);
        Assert.Empty(small.GroqHttp.Bodies);
        Assert.False(smallFit.Report.Reduced);
        Assert.All(smallInput.Artifacts, a => Assert.Contains($"{a.Header}: {a.Content}", smallFit.UserPrompt));

        // Large project: Gemini has no request budget, so only the standard limits apply - and are reported.
        var large = new Providers();
        var input = CopilotContextFitterTests.Input();
        var (_, _, fit) = await AskCopilotAsync(large, input);
        var user = GeminiMessages(Assert.Single(large.GeminiHttp.Bodies)).User;
        Assert.Equal(StandardCopilotPrompt(input), user);
        Assert.Equal(fit.UserPrompt, user);
        Assert.Empty(large.GroqHttp.Bodies);
        Assert.Equal(CopilotContextReport.StandardLimits, fit.Report.Reason);
        Assert.Null(fit.Report.Provider);
        CopilotContextFitterTests.AssertQuestionVerbatimOnce(user, CopilotQuestion);
        CopilotContextFitterTests.AssertReportMatchesPrompt(input, fit);
    }

    [Fact]
    public async Task Copilot_small_prompt_on_groq_is_sent_as_built_with_no_provider_budget_reduction()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        var input = CopilotContextFitterTests.Input(artifacts: 8, contentChars: 300, versions: 3, totalVersions: 3, excerpts: 2);

        var (_, _, fit) = await AskCopilotAsync(providers, input);

        var user = GroqMessages(Assert.Single(providers.GroqHttp.Bodies)).User;
        Assert.Equal(StandardCopilotPrompt(input), user);
        Assert.DoesNotContain(OmissionText, user);
        Assert.False(fit.Report.Reduced);
        Assert.Empty(providers.GeminiHttp.Bodies);
        CopilotContextFitterTests.AssertQuestionVerbatimOnce(user, CopilotQuestion);
    }

    [Fact]
    public async Task Copilot_large_context_on_a_groq_only_route_is_fitted_and_sent_once_deterministically()
    {
        string? previous = null;
        for (var run = 0; run < 2; run++)
        {
            var providers = new Providers(groqOnly: true);
            var input = CopilotContextFitterTests.Input(impact: CopilotContextFitterTests.Impact(4, 6));

            var (_, _, fit) = await AskCopilotAsync(providers, input);

            // The standard prompt was refused before sending; only the fitted one went to Groq, the
            // route's only provider.
            var (system, user) = GroqMessages(Assert.Single(providers.GroqHttp.Bodies));
            Assert.Empty(providers.GeminiHttp.Bodies);
            Assert.Equal(fit.UserPrompt, user);
            Assert.True(PromptBudgetReducer.EstimateRequestTokens(system, user) <= InputBudget - CopilotContextFitter.RepairReserveTokens);
            Assert.DoesNotContain(OmissionText, user);
            CopilotContextFitterTests.AssertQuestionVerbatimOnce(user, CopilotQuestion);
            Assert.Equal(CopilotContextReport.ProviderBudget, fit.Report.Reason);
            Assert.Equal("Groq", fit.Report.Provider);
            Assert.Equal(InputBudget, fit.Report.InputBudget);
            CopilotContextFitterTests.AssertReportMatchesPrompt(input, fit);

            if (previous is not null) Assert.Equal(previous, user);
            previous = user;
        }
    }

    [Fact]
    public async Task Copilot_repair_after_fitting_is_sent_whole_and_never_refitted()
    {
        var providers = new Providers(groqOnly: true);
        var answers = 0;
        providers.GroqContent = (system, user) => answers++ == 0 ? "this is not json" : StubAnswer(system, user);
        var input = CopilotContextFitterTests.Input();

        var (_, _, fit) = await AskCopilotAsync(providers, input);

        Assert.Equal(2, providers.GroqHttp.Bodies.Count);
        var first = GroqMessages(providers.GroqHttp.Bodies[0]);
        var repair = GroqMessages(providers.GroqHttp.Bodies[1]);
        Assert.Equal(fit.UserPrompt, first.User);
        Assert.StartsWith(first.User, repair.User);
        Assert.Contains("Your previous response was rejected", repair.User);
        Assert.True(PromptBudgetReducer.EstimateRequestTokens(repair.System, repair.User) <= InputBudget);
        Assert.Empty(providers.GeminiHttp.Bodies);
    }

    [Fact]
    public async Task Copilot_repair_attempt_over_the_budget_is_refused_without_a_refit_or_second_request()
    {
        // One artifact sized so the standard prompt just fits Groq's budget but its repair does not.
        CopilotPromptInput? input = null;
        for (var chars = 6_000; chars < 12_000 && input is null; chars++)
        {
            var candidate = CopilotContextFitterTests.Input(artifacts: 1, contentChars: chars, versions: 0, totalVersions: 0, excerpts: 0);
            var tokens = CopilotContextFitter.StandardFit(CopilotService.SystemPrompt, candidate).EstimatedTokens;
            if (tokens is > InputBudget - 20 and <= InputBudget) input = candidate;
        }

        Assert.NotNull(input);
        var providers = new Providers();
        providers.CoolDownGemini();
        providers.GroqContent = (_, _) => "this is not json";

        var ex = await Assert.ThrowsAsync<CopilotContextOverBudgetException>(() => AskCopilotAsync(providers, input!));

        Assert.Equal(CopilotRefusalReason.RepairAttempt, ex.Reason);
        Assert.Equal(StandardCopilotPrompt(input!), GroqMessages(Assert.Single(providers.GroqHttp.Bodies)).User);
        Assert.StartsWith("ProjectCopilotQuery: the first request was sent to the AI provider but its response failed validation", ex.Message);
        Assert.Empty(providers.GeminiHttp.Bodies);
        AssertNoCopilotText(ex.Message, input!);
    }

    [Fact]
    public async Task Copilot_required_content_over_the_budget_is_refused_before_any_provider_call()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        var input = CopilotContextFitterTests.Input(facts: CopilotContextFitterTests.Text("fact", 12_000));

        var ex = await Assert.ThrowsAsync<CopilotContextOverBudgetException>(() => AskCopilotAsync(providers, input));

        Assert.Equal(CopilotRefusalReason.RequiredContextTooLarge, ex.Reason);
        Assert.Equal("Groq", ex.Provider);
        Assert.Equal(InputBudget, ex.InputBudget);
        Assert.True(ex.EstimatedInputTokens > InputBudget);
        Assert.Equal("Gemini", ex.Primary!.Provider);
        Assert.Contains("The question is never shortened", ex.Message);
        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.Empty(providers.GeminiHttp.Bodies);
        AssertNoCopilotText(ex.Message, input);
    }

    // Every route that includes Gemini: the shortened prompt is never sent to any provider, so Gemini
    // can only ever have received the complete standard prompt.
    [Theory]
    [InlineData("cooldown")]
    [InlineData("dailyQuota")]
    [InlineData("outage")]
    [InlineData("groqPrimary")]
    public async Task Copilot_never_sends_a_shortened_prompt_on_a_route_that_includes_gemini(string scenario)
    {
        var providers = scenario == "groqPrimary" ? new Providers(groqPrimary: true) : new Providers();
        if (scenario == "cooldown") providers.CoolDownGemini();
        providers.GeminiQuota = scenario == "dailyQuota";
        providers.GeminiOutage = scenario == "outage";
        var input = CopilotContextFitterTests.Input();

        var ex = await Assert.ThrowsAsync<CopilotContextOverBudgetException>(() => AskCopilotAsync(providers, input));

        Assert.Equal(CopilotRefusalReason.RetryNotSafe, ex.Reason);
        Assert.Contains("no shortened request was made", ex.Message);
        Assert.Equal(scenario == "groqPrimary" ? null : "Gemini", ex.Primary?.Provider);
        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.All(providers.GeminiHttp.Bodies, b => Assert.Equal(StandardCopilotPrompt(input), GeminiMessages(b).User));
        Assert.Equal(scenario is "dailyQuota" or "outage", providers.GeminiHttp.Bodies.Count > 0);
        AssertNoCopilotText(ex.Message, input);

        // The next question on the quota route skips Gemini (cool-down) and is refused the same way.
        if (scenario == "dailyQuota")
        {
            var geminiCalls = providers.GeminiHttp.Bodies.Count;
            await Assert.ThrowsAsync<CopilotContextOverBudgetException>(() => AskCopilotAsync(providers, input));
            Assert.Equal(geminiCalls, providers.GeminiHttp.Bodies.Count);
            Assert.Empty(providers.GroqHttp.Bodies);
        }
    }

    // The race the previous check-then-retry left open: Gemini fails (no cool-down recorded), Groq
    // refuses the standard prompt, and by the time a retry would be routed Gemini is healthy again -
    // while the route still reports a cool-down (here deliberately stale). A fitted retry would then
    // reach Gemini with the shortened prompt. It must not be made at all.
    private sealed class StaleCooldownRoute(Providers providers) : IAiRouteInspector
    {
        public AiRouteSnapshot Inspect()
        {
            providers.GeminiOutage = false; // Gemini recovers exactly between the refusal and any retry
            return AiRouteInspector.Inspect(providers.Client) with { PrimaryCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(10) };
        }
    }

    [Fact]
    public async Task Copilot_does_not_retry_when_gemini_could_serve_the_retry_even_if_the_route_reports_a_cooldown()
    {
        var providers = new Providers { GeminiOutage = true };
        var input = CopilotContextFitterTests.Input();

        var ex = await Assert.ThrowsAsync<CopilotContextOverBudgetException>(() => CopilotService.AskModelAsync(
            providers.Client, new StaleCooldownRoute(providers), NullLogger.Instance, input,
            input.Artifacts.Select(a => a.Code), "copilot-test", CancellationToken.None));

        Assert.Equal(CopilotRefusalReason.RetryNotSafe, ex.Reason);
        Assert.False(providers.GeminiOutage); // the route was inspected: the retry decision was reached
        Assert.NotEmpty(providers.GeminiHttp.Bodies);
        Assert.All(providers.GeminiHttp.Bodies, b => Assert.Equal(StandardCopilotPrompt(input), GeminiMessages(b).User));
        Assert.Empty(providers.GroqHttp.Bodies);
    }

    [Fact]
    public void Fitted_retry_is_allowed_only_on_a_single_provider_route_whose_provider_refused()
    {
        var groq = new AiProviderLimits("Groq", "openai/gpt-oss-120b", false, true, InputBudget, MaxRequestTokens, "Ai:Groq:MaxRequestTokens",
            8, "Ai:Groq:MaxChunkedParts", null, null, null, null);
        var gemini = groq with { Provider = "Gemini", InputBudget = null, MaxRequestTokens = null };
        var refused = new AiRequestOverBudgetException("Groq", InputBudget, 9_000);
        var refusedAfterGemini = refused.WithPrimary(new PrimaryProviderOutcome("Gemini", "Gemini was skipped", null));

        Assert.True(CopilotService.FittedRetryStaysWithRefusingProvider(refused, new AiRouteSnapshot(groq, null, null)));
        Assert.False(CopilotService.FittedRetryStaysWithRefusingProvider(refused, new AiRouteSnapshot(groq, gemini, null)));
        Assert.False(CopilotService.FittedRetryStaysWithRefusingProvider(refusedAfterGemini, new AiRouteSnapshot(gemini, groq, DateTimeOffset.UtcNow.AddHours(1))));
        Assert.False(CopilotService.FittedRetryStaysWithRefusingProvider(refused, new AiRouteSnapshot(groq, null, null, "bad configuration")));
        Assert.False(CopilotService.FittedRetryStaysWithRefusingProvider(refused, new AiRouteSnapshot(gemini, null, null)));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copilot_refusal_logs_carry_diagnostics_but_no_question_or_prompt_text(bool requiredTooLarge)
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        var input = requiredTooLarge
            ? CopilotContextFitterTests.Input(facts: CopilotContextFitterTests.Text("fact", 12_000))
            : CopilotContextFitterTests.Input();
        var logger = new RecordingLogger();

        await Assert.ThrowsAsync<CopilotContextOverBudgetException>(() => CopilotService.AskModelAsync(
            providers.Client, new TestRoute(providers.Client), logger, input, input.Artifacts.Select(a => a.Code), "project 42",
            CancellationToken.None));

        var refusal = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("AI stage request refused"));
        var fields = refusal.State.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("ProjectCopilotQuery", fields["OperationType"]);
        Assert.Equal(requiredTooLarge ? CopilotRefusalReason.RequiredContextTooLarge : CopilotRefusalReason.RetryNotSafe, fields["Reason"]);
        Assert.Equal("Groq", fields["Provider"]);
        Assert.Equal("Gemini", fields["PrimaryProvider"]);
        Assert.Equal(InputBudget, fields["InputBudget"]);
        Assert.Equal("project 42", fields["InputReference"]);

        // No log line of the whole call carries the question or prompt content.
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("QSENTINEL", e.Message);
            Assert.DoesNotContain("carry-over", e.Message);
            Assert.DoesNotContain("rule1 clause", e.Message);
            Assert.DoesNotContain("fact clause", e.Message);
        });
    }

    [Fact]
    public async Task Copilot_without_raw_source_access_sends_and_reports_nothing_about_documents()
    {
        var providers = new Providers(groqOnly: true);
        var input = CopilotContextFitterTests.Input(authorized: false);

        var (_, _, fit) = await AskCopilotAsync(providers, input);

        var user = GroqMessages(Assert.Single(providers.GroqHttp.Bodies)).User;
        Assert.DoesNotContain("policy-", user);
        Assert.DoesNotContain("Document excerpts not shown", user);
        Assert.Null(fit.Report.ExcerptsOmitted);
        Assert.Empty(fit.SentExcerpts);
    }

    // ---- Project Copilot through the API (TestHost: needs the test SQL Server) --------------------

    [Fact]
    public async Task Copilot_api_accepts_a_question_at_the_length_limit_and_rejects_a_longer_one_without_a_provider_call()
    {
        // This test is about the question-length limit, not the provider budget: an input budget well
        // above the standard prompt (question + this project's context) keeps the project's incidental
        // context size out of it. Budget refusals are covered by the Copilot budget tests above and below.
        var providers = new Providers(groqOnly: true, maxRequestTokens: ReservedOutputTokens + 4 * InputBudget);
        await using var f = await Fixture.CreateAsync(providers, 2_500, 10);
        var ask = $"/api/projects/{f.ProjectId}/copilot/ask";

        var (limitStatus, limits) = await f.Ba.GetAsync($"/api/projects/{f.ProjectId}/copilot/limits");
        Assert.Equal(200, limitStatus);
        Assert.Equal(2_000, limits!["maxQuestionChars"]!.GetValue<int>());

        var (okStatus, ok) = await f.Ba.PostAsync(ask, new { question = "Which rules apply? " + new string('q', 1_981) });
        Assert.True(okStatus == 200, ok?.ToJsonString());
        Assert.NotNull(ok!["context"]);

        var executions = await f.CountAsync("AIExecutions");
        var groq = providers.GroqHttp.Bodies.Count;
        var tooLong = "QLONGSENTINEL " + new string('q', 1_987);
        Assert.Equal(2_001, tooLong.Length);
        var (status, body) = await f.Ba.PostAsync(ask, new { question = tooLong });

        Assert.Equal(400, status);
        Assert.Equal("validation_failed", body!["code"]!.GetValue<string>());
        Assert.Contains("at most 2,000", body["detail"]!.GetValue<string>());
        Assert.DoesNotContain("QLONGSENTINEL", body.ToJsonString());
        Assert.Equal(groq, providers.GroqHttp.Bodies.Count);
        Assert.Empty(providers.GeminiHttp.Bodies);
        Assert.Equal(executions, await f.CountAsync("AIExecutions"));
    }

    [Fact]
    public async Task Copilot_api_refuses_a_request_whose_required_content_exceeds_the_budget_and_saves_nothing()
    {
        // A 600-token input budget: smaller than the Copilot's system prompt alone.
        var providers = new Providers(maxRequestTokens: ReservedOutputTokens + 600);
        providers.CoolDownGemini();
        await using var f = await Fixture.CreateAsync(providers, 2_500, 10);
        f.Ai.Forward = true;
        var executions = await f.CountAsync("AIExecutions");

        var (status, body) = await f.Ba.PostAsync($"/api/projects/{f.ProjectId}/copilot/ask", new { question = "Which QSENTINEL rules apply?" });

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.Equal("ProjectCopilotQuery", body["stage"]!.GetValue<string>());
        Assert.Equal("RequiredContextTooLarge", body["reason"]!.GetValue<string>());
        Assert.False(body["sentToProvider"]!.GetValue<bool>());
        Assert.Equal(600, body["inputBudget"]!.GetValue<int>());
        Assert.Contains("never shortens your question", body["detail"]!.GetValue<string>());
        Assert.DoesNotContain("QSENTINEL", body.ToJsonString());
        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.Empty(providers.GeminiHttp.Bodies);
        Assert.Equal(executions, await f.CountAsync("AIExecutions"));
    }

    // ---- Review agent: whole or not at all (TestHost: needs the test SQL Server) ------------------

    private sealed class ReviewerUser : ICurrentUser
    {
        public string UserId => "review-agent-test";
        public bool IsInRole(string role) => role == Roles.Administrator;
    }

    private static async Task<AgentOutput> RunReviewAgentAsync(Fixture f)
    {
        using var scope = f.Host.Factory.Services.CreateScope();
        var agent = new ReviewAgent(f.Ai, scope.ServiceProvider.GetRequiredService<IAiReapDbContext>(), new ReviewerUser(),
            scope.ServiceProvider.GetRequiredService<ILifecycleEvidenceService>(), NullLogger<ReviewAgent>.Instance);
        return await agent.RunAsync(new AgentContext(Guid.Parse(f.ProjectId), Guid.Parse(f.SourceId)), CancellationToken.None);
    }

    [Fact]
    public async Task Review_agent_sends_its_complete_prompt_uncondensed_when_it_fits()
    {
        var providers = new Providers();
        providers.CoolDownGemini();
        await using var f = await Fixture.CreateAsync(providers, 2_500, 10, generate: false);

        await RunReviewAgentAsync(f);

        var call = Assert.Single(f.Ai.Calls);
        Assert.True(call.Uncondensed);
        Assert.Contains("readinessSummary", call.System);
        var (system, user) = GroqMessages(Assert.Single(providers.GroqHttp.Bodies));
        Assert.Equal(call.System, system);
        Assert.Equal(call.User, user);
        Assert.Contains("\"checksReviewed\"", user);
        Assert.Equal(1, await f.Host.ScalarAsync("SELECT COUNT(*) FROM AIExecutions WHERE OperationType = 'AgentReviewSummary'"));
    }

    [Fact]
    public async Task Review_agent_over_the_budget_is_refused_before_it_is_sent_with_the_stage_message()
    {
        // A 200-token input budget: smaller than the review prompt.
        var providers = new Providers(maxRequestTokens: ReservedOutputTokens + 200);
        providers.CoolDownGemini();
        await using var f = await Fixture.CreateAsync(providers, 2_500, 10, generate: false);

        var ex = await Assert.ThrowsAsync<AiStageOverBudgetException>(() => RunReviewAgentAsync(f));

        Assert.Equal("AgentReviewSummary", ex.Stage);
        Assert.False(ex.RepairAttempt);
        Assert.Equal(200, ex.InputBudget);
        Assert.StartsWith("AgentReviewSummary: the request is too large to send whole", ex.Message);
        Assert.Contains("so it was not sent", ex.Message);
        Assert.True(Assert.Single(f.Ai.Calls).Uncondensed);
        Assert.Empty(providers.GroqHttp.Bodies);
        Assert.Empty(providers.GeminiHttp.Bodies);
        Assert.Equal(0, await f.Host.ScalarAsync("SELECT COUNT(*) FROM AIExecutions WHERE OperationType = 'AgentReviewSummary'"));
    }
}
