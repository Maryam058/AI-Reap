using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// The dry-run AI plan (GET /api/requirement-sources/{id}/ai-plan): ChunkPlanner must plan exactly
// the parts GenerationSupport.CallAiAndParseChunkedAsync sends, AiRouteInspector must read the
// limits the client chain enforces, and the endpoint must never contact a provider or write.
// Real Gemini/Groq clients over fake HTTP only - never a live API.
public class AiPlanTests
{
    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";
    private const int MaxRequestTokens = 8000;
    private const int ReservedOutputTokens = 4000;
    private const int InputBudget = MaxRequestTokens - ReservedOutputTokens;
    private const int GroqMaxChunkedParts = 8;

    // The estimate logged for the TriageAssessment SRS analysis (~84,308 input tokens).
    private const int LargeSrsRequestTokens = 84_308;

    private const string SystemPrompt = """
        You are a requirements engineer. Respond with ONLY a single JSON object matching exactly this shape:
        {"functionalRequirements": [{"title": "string", "actor": "string"}], "nonFunctionalRequirements": []}
        """;

    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");

    // ---- fake providers --------------------------------------------------------------------

    private sealed record Stack(
        FallbackAiChatClient Client, GroqAiChatClientTests.FakeHandler GeminiHttp, GroqAiChatClientTests.FakeHandler GroqHttp,
        AiProviderCooldown Cooldown);

    // The production chain (DependencyInjection.Wrap/ResolveCloud) over fake HTTP, with the
    // cool-down store exposed so a test can put Gemini in a recorded cool-down.
    private static Stack BuildStack(
        Func<HttpRequestMessage, HttpResponseMessage> geminiRespond, Func<HttpRequestMessage, HttpResponseMessage> groqRespond,
        int maxChunkedParts = GroqMaxChunkedParts)
    {
        var geminiHttp = new GroqAiChatClientTests.FakeHandler(geminiRespond);
        var groqHttp = new GroqAiChatClientTests.FakeHandler(groqRespond);
        var gemini = new GeminiAiChatClient(new HttpClient(geminiHttp),
            Options.Create(new GeminiOptions { ApiKey = GeminiKey, MaxOutputTokens = 65536, TimeoutSeconds = 300 }));
        var groq = new GroqAiChatClient(new HttpClient(groqHttp), Options.Create(new GroqOptions { ApiKey = GroqKey }));
        var resilience = new AiResilienceOptions { BaseDelayMilliseconds = 0 };
        Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;
        var cooldown = new AiProviderCooldown();

        var client = new FallbackAiChatClient(
            new ResilientAiChatClient(gemini, "Gemini", resilience.WithoutRateLimitRetry().WithTimeout(300, 1), NullLogger.Instance, NoDelay), "Gemini",
            new RequestBudgetAiChatClient(
                new ResilientAiChatClient(groq, "Groq", resilience, NullLogger.Instance, NoDelay), "Groq",
                "Ai:Groq:MaxRequestTokens", MaxRequestTokens, ReservedOutputTokens, NullLogger.Instance,
                maxChunkedParts, "Ai:Groq:MaxChunkedParts"), "Groq",
            cooldown, TimeSpan.FromMinutes(10), NullLogger.Instance);
        return new Stack(client, geminiHttp, groqHttp, cooldown);
    }

    // For stacks a plan must never touch: any request fails the test loudly.
    private static HttpResponseMessage NoRequest(HttpRequestMessage request) =>
        throw new InvalidOperationException($"The AI plan sent a provider request: {request.RequestUri}");

    private static HttpResponseMessage GeminiQuota(HttpRequestMessage _) => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new { error = new { code = 429, message = "You exceeded your current quota (per day).", status = "RESOURCE_EXHAUSTED" } })
    };

    private static HttpResponseMessage GroqGeneration(HttpRequestMessage request)
    {
        var part = PartOf(request);
        var content = new JsonObject
        {
            ["functionalRequirements"] = new JsonArray(
                new JsonObject { ["title"] = $"Record leave area {part}", ["actor"] = "HR", ["priority"] = "Medium" }),
            ["nonFunctionalRequirements"] = new JsonArray()
        }.ToJsonString();
        return GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content));
    }

    private static int PartOf(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
        var match = PartPattern.Match(messages[1]!["content"]!.GetValue<string>());
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    public class PartShape : IValidatableAiResponse
    {
        public List<JsonObject> FunctionalRequirements { get; set; } = new();
        public virtual void Validate(AiResponseValidator v) { }
    }

    public sealed class WholeShape : PartShape
    {
        public override void Validate(AiResponseValidator v)
        {
            if (FunctionalRequirements.Count == 0) v.Fail("No functional requirements.");
        }
    }

    private static List<string> Clarifications(int count) =>
        Enumerable.Range(1, count).Select(i =>
            $"- Q: Who approves leave requests for leave area {1 + (i - 1) % 12} when the approver of case {i} is absent?\n" +
            "  A: (not yet answered - treat as still unresolved, propose an assumption if needed and flag it)\n").ToList();

    private const string Objectives = "\n\nBusiness objectives of this project:\n- BO-001: Cut leave admin time\n";

    // ---- ChunkPlanner = what the real chunked call sends -------------------------------------

    [Theory]
    [InlineData(3_000, 0)]     // fits Groq whole
    [InlineData(20_000, 0)]
    [InlineData(20_000, 12)]   // clarifications go whole with every part
    [InlineData(40_000, 60)]
    [InlineData(78_561, 60)]   // clarifications are split across parts
    public async Task Planned_part_count_is_the_number_of_requests_a_real_chunked_run_sends(int requestTokens, int clarifications)
    {
        var source = ChunkedAnalysisTests.StructuredSource(requestTokens, SystemPrompt);
        var shared = new GenerationSupport.SharedContext(ClarificationContextBuilder.Heading, Clarifications(clarifications));

        var plan = ChunkPlanner.Plan(SystemPrompt, source, Objectives, shared, InputBudget, maxParts: 0);

        var stack = BuildStack(GeminiQuota, GroqGeneration, maxChunkedParts: 0);
        var parts = await GenerationSupport.CallAiAndParseChunkedAsync<WholeShape, PartShape>(
            stack.Client, NullLogger.Instance, "RequirementGeneration", "src-1", SystemPrompt, source, CancellationToken.None,
            fixedContext: Objectives, delay: (_, _) => Task.CompletedTask, sharedContext: shared);

        Assert.True(plan.IsSendable);
        Assert.Equal(plan.PartCount, parts.Count);
        Assert.Equal(plan.PartCount, stack.GroqHttp.Requests.Count);
        if (plan.Outcome == ChunkPlanOutcome.Split)
        {
            // Each request carries the planned part: its header and the start of its planned text.
            for (var i = 0; i < plan.Chunks.Count; i++)
            {
                var user = JsonNode.Parse(stack.GroqHttp.Bodies[i])!["messages"]![1]!["content"]!.GetValue<string>();
                Assert.StartsWith(GenerationSupport.PartHeader(i + 1, plan.Chunks.Count), user);
                Assert.Contains(plan.Chunks[i].Split('\n')[0], user);
            }
        }
        else
        {
            Assert.Equal(ChunkPlanOutcome.Whole, plan.Outcome);
            Assert.Equal(1, plan.PartCount);
        }
    }

    [Fact]
    public async Task Planned_part_limit_refusal_is_the_refusal_a_real_run_raises_before_any_request()
    {
        var source = ChunkedAnalysisTests.StructuredSource(40_000, SystemPrompt);
        var plan = ChunkPlanner.Plan(SystemPrompt, source, string.Empty, GenerationSupport.SharedContext.None, InputBudget, GroqMaxChunkedParts);

        var stack = BuildStack(GeminiQuota, GroqGeneration);
        var ex = await Assert.ThrowsAsync<AiChunkLimitExceededException>(() =>
            GenerationSupport.CallAiAndParseChunkedAsync<WholeShape, PartShape>(
                stack.Client, NullLogger.Instance, "RequirementAnalysis", "src-1", SystemPrompt, source, CancellationToken.None));

        Assert.Equal(ChunkPlanOutcome.TooManyParts, plan.Outcome);
        Assert.Equal(plan.Chunks.Count, ex.RequiredParts);
        Assert.Equal(GroqMaxChunkedParts, ex.MaxParts);
        Assert.Empty(stack.GroqHttp.Requests);
    }

    [Fact]
    public void Provider_without_a_budget_is_planned_as_one_whole_request()
    {
        var source = ChunkedAnalysisTests.StructuredSource(LargeSrsRequestTokens, SystemPrompt);

        var plan = ChunkPlanner.Plan(SystemPrompt, source, Objectives, GenerationSupport.SharedContext.None, inputBudget: null, maxParts: 0);

        Assert.Equal(ChunkPlanOutcome.Whole, plan.Outcome);
        Assert.Equal(1, plan.PartCount);
        Assert.Equal(PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, source + Objectives), plan.EstimatedWholeTokens);
    }

    [Fact]
    public void Budget_too_small_for_any_source_text_is_planned_as_cannot_split()
    {
        var source = ChunkedAnalysisTests.StructuredSource(10_000, SystemPrompt);

        var plan = ChunkPlanner.Plan(SystemPrompt, source, string.Empty, GenerationSupport.SharedContext.None, inputBudget: 1_200, maxParts: 0);

        Assert.Equal(ChunkPlanOutcome.CannotSplit, plan.Outcome);
        Assert.False(plan.IsSendable);
        Assert.Empty(plan.Chunks);
    }

    // ---- AiRouteInspector -------------------------------------------------------------------

    [Fact]
    public void Inspector_reads_the_limits_the_client_chain_enforces_without_sending_a_request()
    {
        var stack = BuildStack(NoRequest, NoRequest);

        var route = AiRouteInspector.Inspect(stack.Client);

        Assert.Equal("Gemini", route.Primary!.Provider);
        Assert.Null(route.Primary.InputBudget);
        Assert.Equal(65536, route.Primary.MaxOutputTokens);
        Assert.Equal(300, route.Primary.TimeoutSeconds);
        Assert.Equal(30_000, route.Primary.LargeGenerationWarningTokens);
        Assert.Equal("Groq", route.Fallback!.Provider);
        Assert.Equal(InputBudget, route.Fallback.InputBudget);
        Assert.Equal(MaxRequestTokens, route.Fallback.MaxRequestTokens);
        Assert.Equal(GroqMaxChunkedParts, route.Fallback.MaxParts);
        Assert.Equal("Ai:Groq:MaxChunkedParts", route.Fallback.MaxPartsSetting);
        Assert.Null(route.PrimaryCooldownUntil);
        Assert.Empty(stack.GeminiHttp.Requests);
        Assert.Empty(stack.GroqHttp.Requests);

        var until = DateTimeOffset.UtcNow.AddMinutes(9);
        stack.Cooldown.CoolDown("Gemini", until);
        Assert.Equal(until, AiRouteInspector.Inspect(stack.Client).PrimaryCooldownUntil);
    }

    private sealed class PlainClient : IAiChatClient
    {
        public string ModelName => "plain-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("must not be called");
    }

    private sealed class OpaqueBudgetClient : IUncondensedAiChatClient
    {
        public string ModelName => "opaque-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("must not be called");
        public Task<string> CompleteUncondensedAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("must not be called");
    }

    [Fact]
    public void Inspector_treats_a_client_without_a_budget_as_whole_and_an_unreadable_budget_as_unknown()
    {
        var plain = AiRouteInspector.Inspect(new PlainClient());
        Assert.True(plain.Primary!.LimitsKnown);
        Assert.Null(plain.Primary.InputBudget);
        Assert.Null(plain.Fallback);

        var opaque = AiRouteInspector.Inspect(new OpaqueBudgetClient());
        Assert.False(opaque.Primary!.LimitsKnown);
    }

    // ---- the endpoint -----------------------------------------------------------------------

    [Fact]
    public async Task Analyze_plan_for_a_large_srs_reports_the_groq_part_limit_without_contacting_any_provider_or_writing()
    {
        var stack = BuildStack(NoRequest, NoRequest);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(LargeSrsRequestTokens));
        var artifactsBefore = (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.ToJsonString();
        var auditBefore = (await ba.GetAsync($"/api/projects/{projectId}/ai-executions")).Body!.ToJsonString();
        var sourcesBefore = (await ba.GetAsync($"/api/projects/{projectId}/requirement-sources")).Body!.AsArray().Count;

        var (status, plan) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=analyze");

        Assert.Equal(200, status);
        Assert.Equal("analyze", plan!["operation"]!.GetValue<string>());
        var gemini = Provider(plan, "Gemini");
        Assert.Equal("Primary", gemini["role"]!.GetValue<string>());
        Assert.True(gemini["wholeRequest"]!.GetValue<bool>());
        Assert.Equal("QuotaUnknown", gemini["availability"]!.GetValue<string>());
        Assert.Equal("WithinLimits", gemini["status"]!.GetValue<string>());
        Assert.Contains(gemini["reasons"]!.AsArray(), r => r!["code"]!.GetValue<string>() == "QuotaUnknown");

        var groq = Provider(plan, "Groq");
        Assert.Equal("Blocked", groq["status"]!.GetValue<string>());
        Assert.False(groq["wholeRequest"]!.GetValue<bool>());
        var tooMany = Assert.Single(groq["reasons"]!.AsArray(), r => r!["code"]!.GetValue<string>() == "TooManyParts")!;
        Assert.Equal("Ai:Groq:MaxChunkedParts", tooMany["setting"]!.GetValue<string>());
        Assert.Equal(8, tooMany["limit"]!.GetValue<int>());
        Assert.True(tooMany["actual"]!.GetValue<int>() > 8);
        Assert.Equal(groq["parts"]!["min"]!.GetValue<int>(), groq["parts"]!["max"]!.GetValue<int>()); // no excerpts: exact
        Assert.Equal(tooMany["actual"]!.GetValue<int>(), groq["parts"]!["max"]!.GetValue<int>());

        // Gemini is tried first and its quota is unknown, so the plan warns rather than blocks.
        Assert.Equal("Warning", plan["overall"]!["status"]!.GetValue<string>());
        Assert.Contains("quota is unknown", plan["overall"]!["summary"]!.GetValue<string>());
        Assert.Contains(plan["caveats"]!.AsArray(), c => c!.GetValue<string>().StartsWith("No AI provider was contacted"));

        Assert.Empty(stack.GeminiHttp.Requests);
        Assert.Empty(stack.GroqHttp.Requests);
        Assert.Equal(artifactsBefore, (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.ToJsonString());
        Assert.Equal(auditBefore, (await ba.GetAsync($"/api/projects/{projectId}/ai-executions")).Body!.ToJsonString());
        Assert.Equal(sourcesBefore, (await ba.GetAsync($"/api/projects/{projectId}/requirement-sources")).Body!.AsArray().Count);
    }

    [Fact]
    public async Task Generate_plan_while_gemini_cools_down_is_blocked_by_the_groq_limit_and_records_no_objectives()
    {
        var stack = BuildStack(NoRequest, NoRequest);
        stack.Cooldown.CoolDown("Gemini", DateTimeOffset.UtcNow.AddMinutes(9));
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(LargeSrsRequestTokens));

        // Objectives text not yet recorded as BO artifacts (project create/update would record it),
        // so a write by the plan would show up as new artifacts.
        await host.ExecuteAsync(
            $"UPDATE Projects SET Objectives = N'Cut triage time' + CHAR(10) + N'Keep assessments auditable' WHERE Id = '{projectId}'");

        var (status, plan) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=generate");

        Assert.Equal(200, status);
        Assert.Equal("Blocked", plan!["overall"]!["status"]!.GetValue<string>());
        Assert.Equal("Groq", plan["route"]!["expectedFirst"]!.GetValue<string>());
        Assert.NotNull(plan["route"]!["primaryCooldownUntil"]);
        Assert.Equal(2, plan["context"]!["businessObjectives"]!.GetValue<int>());

        var gemini = Provider(plan, "Gemini");
        Assert.Equal("CoolingDown", gemini["availability"]!.GetValue<string>());
        Assert.Equal("Blocked", gemini["status"]!.GetValue<string>());
        Assert.Equal("Warning", gemini["sizeStatus"]!.GetValue<string>());
        Assert.Contains(gemini["reasons"]!.AsArray(), r => r!["code"]!.GetValue<string>() == "CoolingDown");
        Assert.Contains(gemini["reasons"]!.AsArray(), r => r!["code"]!.GetValue<string>() == "LargeResponseRisk");

        var groq = Provider(plan, "Groq");
        Assert.Equal("Blocked", groq["status"]!.GetValue<string>());
        Assert.Contains(groq["reasons"]!.AsArray(), r => r!["code"]!.GetValue<string>() == "TooManyParts");

        // Objectives were previewed for the prompt, not recorded; no provider was contacted.
        Assert.Empty((await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray());
        Assert.Empty(stack.GeminiHttp.Requests);
        Assert.Empty(stack.GroqHttp.Requests);
    }

    [Fact]
    public async Task Generate_plan_part_range_contains_the_parts_a_real_generation_sends()
    {
        var stack = BuildStack(GeminiQuota, GroqGeneration, maxChunkedParts: 0);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(20_000),
            objectives: "Cut leave admin time");
        await UploadNotesAsync(ba, projectId);

        var (_, plan) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=generate");
        var groq = Provider(plan!, "Groq");
        var min = groq["parts"]!["min"]!.GetValue<int>();
        var max = groq["parts"]!["max"]!.GetValue<int>();
        Assert.Equal(1, plan!["context"]!["documentExcerptCandidates"]!.GetValue<int>());
        Assert.Empty(stack.GeminiHttp.Requests);
        Assert.Empty(stack.GroqHttp.Requests);

        var (status, _) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.InRange(stack.GroqHttp.Requests.Count, min, max);
        Assert.Equal(stack.GroqHttp.Requests.Count + 1, stack.GeminiHttp.Requests.Count); // Gemini stays primary: whole source, then each part
        Assert.True(min > 1, "The source should need several Groq parts.");
    }

    [Fact]
    public async Task Plan_endpoint_validates_the_operation_and_is_limited_to_project_members()
    {
        var stack = BuildStack(NoRequest, NoRequest);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var outsider = await host.RegisterAsync("BusinessAnalyst");
        var reviewer = await host.RegisterAsync("Reviewer");
        var (projectId, sourceId) = await CreateSourceAsync(ba, "The system must let employees request leave.");
        Assert.InRange((await host.AddMemberAsync(ba, projectId, reviewer)).Status, 200, 201);

        Assert.Equal(400, (await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=design")).Status);
        Assert.Equal(400, (await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan")).Status);
        Assert.Equal(404, (await ba.GetAsync($"/api/requirement-sources/{Guid.NewGuid()}/ai-plan?operation=analyze")).Status);
        Assert.Equal(403, (await outsider.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=analyze")).Status);

        var (status, plan) = await reviewer.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=Generate");
        Assert.Equal(200, status);
        Assert.Equal("WithinLimits", plan!["overall"]!["status"]!.GetValue<string>());
        Assert.True(Provider(plan, "Groq")["wholeRequest"]!.GetValue<bool>());
        Assert.Empty(stack.GeminiHttp.Requests);
        Assert.Empty(stack.GroqHttp.Requests);
    }

    private static JsonNode Provider(JsonNode plan, string name) =>
        Assert.Single(plan["providers"]!.AsArray(), p => p!["provider"]!.GetValue<string>() == name)!;

    private static async Task<(string ProjectId, string SourceId)> CreateSourceAsync(ApiUser ba, string rawText, string? objectives = null)
    {
        var (ps, project) = await ba.PostAsync("/api/projects", new { name = "AI plan", description = "d", objectives });
        Assert.Equal(201, ps);
        var projectId = project!["id"]!.GetValue<string>();
        var (ss, created) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText });
        Assert.InRange(ss, 200, 201);
        return (projectId, created!["id"]!.GetValue<string>());
    }

    private static async Task UploadNotesAsync(ApiUser ba, string projectId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "Leave Management System meeting notes: managers approve every leave request for each leave area; " +
            "the system must record the dates, type and approver of every leave request."));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "meeting-notes.txt");
        Assert.Equal(HttpStatusCode.Created, (await ba.Http.PostAsync($"/api/projects/{projectId}/documents/upload", form)).StatusCode);
    }
}
