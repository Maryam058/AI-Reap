using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// Regression for "The requirement source is too large for the configured Groq request budget
// (Ai:Groq:MaxRequestTokens=8000 tokens; this request needs about 78,561)". Chunked analysis of a
// source that size yields one set of clarification questions per part; requirement generation then
// sent source + every clarification through the condensing path, where the clarification list is
// never dropped - once it alone outgrew Groq's 4,000-token input budget the request was refused
// (and when it did fit, ~98% of the source was dropped to make room). Requirement generation now
// goes through GenerationSupport.CallAiAndParseChunkedAsync: Gemini still gets the whole prompt,
// a Groq fallback gets bounded parts, each with the clarifications relevant to it.
// Real Gemini/Groq clients over fake HTTP (GroqRequestBudgetTests.BuildStack) - never a live API.
public class ChunkedRequirementGenerationTests
{
    private const int MaxRequestTokens = 8000;
    private const int ReservedOutputTokens = 4000;
    private const int InputBudget = MaxRequestTokens - ReservedOutputTokens;
    private const int ReportedRequestTokens = 78_561;

    private const string SystemPrompt = """
        You are a requirements engineer. Respond with ONLY a single JSON object matching exactly this shape:
        {"functionalRequirements": [{"title": "string", "actor": "string"}], "nonFunctionalRequirements": []}
        """;

    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");

    // ---- inputs ----------------------------------------------------------------------------

    // As many clarifications as a chunked analysis of a ~78k-token source produces (a handful per
    // part), in ClarificationContextBuilder's format; each names the leave area it is about.
    private static List<string> Clarifications(int count, int areas) =>
        Enumerable.Range(1, count).Select(i =>
            $"- Q: Who approves leave requests for leave area {1 + (i - 1) % areas} when the approver of case {i} is absent?\n" +
            "  A: (not yet answered - treat as still unresolved, propose an assumption if needed and flag it)\n").ToList();

    private static GenerationSupport.SharedContext Shared(List<string> items) =>
        new(ClarificationContextBuilder.Heading, items);

    private static int Tokens(GenerationSupport.SharedContext shared) => PromptBudgetReducer.EstimateTokens(shared.RenderAll());

    // ---- fake providers --------------------------------------------------------------------

    private static HttpResponseMessage GroqOk(string content) => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content));

    private static HttpResponseMessage GeminiOk(string text) => GroqAiChatClientTests.Json(new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = "STOP" } }
    });

    private static HttpResponseMessage GeminiQuota() => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new { error = new { code = 429, message = "You exceeded your current quota (per day).", status = "RESOURCE_EXHAUSTED" } })
    };

    // Every part restates one shared FR and NFR (merged away) and adds one FR of its own.
    private static string PartGeneration(int part) => new JsonObject
    {
        ["functionalRequirements"] = new JsonArray(
            new JsonObject { ["title"] = part % 2 == 0 ? "Request leave." : "Request Leave", ["actor"] = "Employee", ["priority"] = "High" },
            new JsonObject { ["title"] = $"Record leave area {part}", ["actor"] = "HR", ["priority"] = "Medium" }),
        ["nonFunctionalRequirements"] = new JsonArray(
            new JsonObject { ["title"] = "Leave records retained seven years", ["category"] = "Compliance", ["description"] = "Retention" })
    }.ToJsonString();

    // Analysis of one part: five open questions specific to that part.
    private static string PartAnalysis(int part) => new JsonObject
    {
        ["actors"] = new JsonArray("Employee"),
        ["capabilities"] = new JsonArray("Request leave"),
        ["dataElements"] = new JsonArray(),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(Enumerable.Range(1, 5).Select(k => (JsonNode)new JsonObject
        {
            ["topic"] = $"Leave area {part} rule {k}",
            ["question"] = $"Which approver handles rule {k} of leave area {part} requests?",
            ["reason"] = "Not stated"
        }).ToArray())
    }.ToJsonString();

    private static (string System, string User) GroqMessages(string body)
    {
        var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
        return (messages[0]!["content"]!.GetValue<string>(), messages[1]!["content"]!.GetValue<string>());
    }

    private static int PartOf(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var match = PartPattern.Match(GroqMessages(body).User);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static void AssertWithinGroqBudget(string body)
    {
        var (system, user) = GroqMessages(body);
        var input = PromptBudgetReducer.EstimateRequestTokens(system, user);
        var maxCompletion = JsonNode.Parse(body)!["max_completion_tokens"]!.GetValue<int>();
        Assert.True(input <= InputBudget, $"Groq input of ~{input} tokens exceeds the {InputBudget}-token input budget.");
        Assert.True(input + maxCompletion <= MaxRequestTokens,
            $"Groq request of ~{input} input + {maxCompletion} max_completion_tokens exceeds MaxRequestTokens {MaxRequestTokens}.");
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

    private static Task<IReadOnlyList<GenerationSupport.AiPart<PartShape>>> Chunked(
        IAiChatClient client, string source, GenerationSupport.SharedContext shared, string fixedContext = "") =>
        GenerationSupport.CallAiAndParseChunkedAsync<WholeShape, PartShape>(
            client, NullLogger.Instance, "RequirementGeneration", "src-1", SystemPrompt, source, CancellationToken.None,
            fixedContext: fixedContext, delay: (_, _) => Task.CompletedTask, sharedContext: shared);

    // ---- the reported failure ----------------------------------------------------------------

    // Before the fix: the whole ~78,561-token prompt (source + clarifications) through Groq's
    // condensing path is refused without a request, because the clarification list alone exceeds
    // the input budget. This is the path requirement generation used to take.
    [Fact]
    public async Task Condensing_path_refuses_the_reported_78561_token_request_without_calling_groq()
    {
        var shared = Shared(Clarifications(120, 40));
        Assert.True(Tokens(shared) > InputBudget);
        var source = ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens - Tokens(shared), SystemPrompt);
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), _ => GroqOk(PartGeneration(1)));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync(SystemPrompt, source + shared.RenderAll()));

        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("Ai:Groq:MaxRequestTokens=8000", ex.Message);
        Assert.Matches(@"needs about 78,56\d", ex.Message);
        Assert.Empty(groqHttp.Requests);
    }

    [Fact]
    public async Task Source_of_about_78561_tokens_on_groq_is_split_into_bounded_parts_carrying_every_clarification()
    {
        var shared = Shared(Clarifications(120, 40));
        var source = ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens - Tokens(shared), SystemPrompt);
        var whole = source + shared.RenderAll();
        Assert.InRange(PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, whole), ReportedRequestTokens - 2, ReportedRequestTokens + 2);
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))));

        var parts = await Chunked(client, source, shared);

        Assert.True(parts.Count >= 20, $"expected >= 20 parts, got {parts.Count}");
        Assert.Equal(Enumerable.Range(1, parts.Count), parts.Select(p => p.Number));
        Assert.All(parts, p => Assert.Equal("groq:openai/gpt-oss-120b", p.Model));
        Assert.Equal(parts.Count + 1, geminiHttp.Requests.Count); // Gemini stays primary: whole source, then each part
        Assert.Equal(parts.Count, groqHttp.Bodies.Count);         // one Groq request per part - nothing oversized sent, nothing re-sent

        var users = groqHttp.Bodies.Select(b => { AssertWithinGroqBudget(b); return GroqMessages(b).User; }).ToList();
        Assert.All(users, u => Assert.DoesNotContain("omitted", u)); // never condensed

        // Every source requirement in exactly one part; every clarification in at least one part.
        foreach (var id in Regex.Matches(source, @"REQ-\d+\.\d+").Select(m => m.Value))
        {
            Assert.Equal(1, users.Count(u => Regex.IsMatch(u, Regex.Escape(id) + @"\b")));
        }

        foreach (var item in shared.Items)
        {
            Assert.Contains(users, u => u.Contains(item, StringComparison.Ordinal));
        }

        // A part shows the clarifications about its own leave areas, and says it shows a subset.
        var areaOne = users.First(u => u.Contains("## 1. Leave area 1\n"));
        Assert.Contains("leave area 1 when", areaOne);
        Assert.Matches(@"\[AI-REAP note: \d+ of the 120 entries of this list are shown with this part", areaOne);
    }

    [Fact]
    public async Task Gemini_available_gets_the_whole_prompt_once_exactly_as_before()
    {
        var shared = Shared(Clarifications(120, 40));
        var source = ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens - Tokens(shared), SystemPrompt);
        const string fixedContext = "\n\nBusiness objectives of this project:\n- BO-001: Cut leave admin\n";
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiOk(PartGeneration(1)), _ => GroqOk(PartGeneration(1)));

        var parts = await Chunked(client, source, shared, fixedContext);

        Assert.Single(parts);
        var sent = JsonNode.Parse(Assert.Single(geminiHttp.Bodies))!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(source + shared.RenderAll() + fixedContext, sent);
        Assert.Empty(groqHttp.Requests);
    }

    [Fact]
    public async Task Small_clarification_list_goes_whole_with_every_part()
    {
        var shared = Shared(Clarifications(3, 3));
        var source = ChunkedAnalysisTests.StructuredSource(20_000, SystemPrompt);
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))));

        var parts = await Chunked(client, source, shared);

        Assert.True(parts.Count > 1);
        Assert.All(groqHttp.Bodies, b =>
        {
            AssertWithinGroqBudget(b);
            var user = GroqMessages(b).User;
            Assert.Contains(shared.RenderAll(), user);
            Assert.DoesNotContain("entries of this list are shown", user);
        });
    }

    [Fact]
    public async Task Clarifications_that_cannot_all_be_shown_are_refused_before_any_groq_request()
    {
        // ~300 tokens per clarification, far more than all parts together have room for.
        var items = Enumerable.Range(1, 300).Select(i => $"- Q: Question {i}?\n  A: {new string('a', 900)} {i}\n").ToList();
        var source = ChunkedAnalysisTests.StructuredSource(12_000, SystemPrompt);
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), _ => GroqOk(PartGeneration(1)));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Chunked(client, source, Shared(items)));

        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("of its 300 clarifications do not fit", ex.Message);
        Assert.Contains("Nothing was sent", ex.Message);
        Assert.Empty(groqHttp.Requests);
    }

    // ---- Ai:Groq:MaxChunkedParts: a run too long for Groq's quota is refused up front -----------

    private const int DefaultMaxChunkedParts = 8; // GroqOptions / appsettings.json default

    // Sizes (estimated source tokens, with SystemPrompt above) that split into 8 and 9 parts for Groq.
    private const int EightPartSourceTokens = 21_000;
    private const int NinePartSourceTokens = 24_500;

    [Fact]
    public async Task More_parts_than_MaxChunkedParts_are_refused_before_any_groq_request()
    {
        var (unlimited, _, unlimitedGroq) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))));
        var source = ChunkedAnalysisTests.StructuredSource(NinePartSourceTokens, SystemPrompt);
        Assert.Equal(9, (await Chunked(unlimited, source, Shared([]))).Count); // the size really needs 9 parts
        Assert.Equal(9, unlimitedGroq.Requests.Count);

        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(
            _ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))), DefaultMaxChunkedParts);

        var ex = await Assert.ThrowsAsync<AiChunkLimitExceededException>(() => Chunked(client, source, Shared([])));

        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.ProviderStatusCode);
        Assert.Equal("Groq", ex.Provider);
        Assert.Equal(9, ex.RequiredParts);
        Assert.Equal(8, ex.MaxParts);
        Assert.Equal("Ai:Groq:MaxChunkedParts", ex.MaxPartsSetting);
        Assert.Contains("sent to Groq in 9 parts, more than the 8 allowed (Ai:Groq:MaxChunkedParts=8)", ex.Message);
        Assert.Contains("about 72,000 tokens (9 requests of up to 8,000 tokens each)", ex.Message);
        Assert.Contains("at least about 9 minutes", ex.Message);
        Assert.Contains("These are estimates, not a check of the remaining Groq quota.", ex.Message);
        Assert.Contains("Nothing was sent and nothing was saved.", ex.Message);
        Assert.Empty(groqHttp.Requests);                   // the guard runs before the first part
        Assert.Single(geminiHttp.Requests);                // only the whole-source attempt, as before
    }

    // The refusal leads with the actual blocker - Gemini's used-up daily quota - not only Groq's
    // part limit, and still sends nothing to Groq.
    [Fact]
    public async Task Part_limit_refusal_after_a_gemini_daily_quota_names_the_gemini_failure()
    {
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(
            _ => GeminiDailyQuota(), r => GroqOk(PartGeneration(PartOf(r))), DefaultMaxChunkedParts);

        var ex = await Assert.ThrowsAsync<AiChunkLimitExceededException>(() =>
            Chunked(client, ChunkedAnalysisTests.StructuredSource(NinePartSourceTokens, SystemPrompt), Shared([])));

        Assert.StartsWith(
            "Primary AI provider not used - Gemini failed: Gemini quota exhausted (daily or zero-limit quota; will not recover " +
            "within this request). (HTTP 429 RESOURCE_EXHAUSTED: You exceeded your current quota.). " +
            "The requirement source would have to be sent to Groq in 9 parts, more than the 8 allowed",
            ex.Message);
        Assert.Contains("Nothing was sent and nothing was saved.", ex.Message);
        var primary = Assert.IsType<PrimaryProviderOutcome>(ex.Primary);
        Assert.Equal("Gemini", primary.Provider);
        Assert.True(primary.Failure!.QuotaExhausted);
        Assert.Equal((AiFailureKind.RequestRejected, 9, 8), (ex.Kind, ex.RequiredParts, ex.MaxParts)); // classification unchanged
        Assert.Single(geminiHttp.Requests);
        Assert.Empty(groqHttp.Requests);
    }

    [Fact]
    public async Task Exactly_MaxChunkedParts_parts_are_sent_as_before()
    {
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(
            _ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))), DefaultMaxChunkedParts);

        var parts = await Chunked(client, ChunkedAnalysisTests.StructuredSource(EightPartSourceTokens, SystemPrompt), Shared([]));

        Assert.Equal(8, parts.Count);
        Assert.Equal(Enumerable.Range(1, 8), parts.Select(p => p.Number));
        Assert.Equal(8, groqHttp.Bodies.Count);
        Assert.All(groqHttp.Bodies, AssertWithinGroqBudget);
    }

    [Fact]
    public async Task MaxChunkedParts_does_not_apply_while_gemini_serves_the_whole_source()
    {
        var shared = Shared(Clarifications(120, 40));
        var source = ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens - Tokens(shared), SystemPrompt);
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(
            _ => GeminiOk(PartGeneration(1)), _ => GroqOk(PartGeneration(1)), DefaultMaxChunkedParts);

        Assert.Single(await Chunked(client, source, shared));
        Assert.Single(geminiHttp.Requests);
        Assert.Empty(groqHttp.Requests);
    }

    // Gemini's free-tier daily quota used up: no retry hint, so Gemini cools down and later parts go
    // straight to Groq - the reported incident.
    private static HttpResponseMessage GeminiDailyQuota() => GroqAiChatClientTests.Json(new
    {
        error = new
        {
            code = 429,
            message = "You exceeded your current quota.",
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
    }, HttpStatusCode.TooManyRequests);

    // A Groq 429 naming its daily token limit, as Groq words it (organization included).
    private const string GroqOrganization = "org_01hzx7qk3rf9tbw5yv2n8c4d6e";

    private static HttpResponseMessage GroqDailyTokenLimit()
    {
        var response = GroqAiChatClientTests.Json(new
        {
            error = new
            {
                message = $"Rate limit reached for model `openai/gpt-oss-120b` in organization `{GroqOrganization}` service tier `on_demand` " +
                          "on tokens per day (TPD): Limit 200000, Used 199500, Requested 7800. Please try again in 1s.",
                type = "tokens",
                code = "rate_limit_exceeded"
            }
        }, HttpStatusCode.TooManyRequests);
        response.Headers.Add("retry-after", "1"); // even a short hint is not waited for on a daily limit
        return response;
    }

    // ---- through the API: analyze, then generate requirements ----------------------------------

    [Fact]
    public async Task Generate_requirements_endpoint_refuses_a_source_over_MaxChunkedParts_and_writes_nothing()
    {
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(
            _ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))), DefaultMaxChunkedParts);
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens, SystemPrompt));
        var artifactsBefore = (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.ToJsonString();
        var auditBefore = (await ba.GetAsync($"/api/projects/{projectId}/ai-executions")).Body!.ToJsonString();

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", problem!["code"]!.GetValue<string>());
        Assert.False(problem["retryable"]!.GetValue<bool>());
        Assert.Equal("The requirement source is too large for Groq.", problem["title"]!.GetValue<string>());
        var detail = problem["detail"]!.GetValue<string>();
        Assert.Matches(@"exceeds the configured Groq processing limit \(it would need \d+ parts; Ai:Groq:MaxChunkedParts=8\)", detail);
        Assert.Contains("No request was sent to Groq and nothing was saved.", detail);
        Assert.False(problem["sentToProvider"]!.GetValue<bool>());
        Assert.True(problem["requiredParts"]!.GetValue<int>() > 8);
        Assert.Empty(groqHttp.Requests);
        Assert.Single(geminiHttp.Requests);
        Assert.Equal(artifactsBefore, (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.ToJsonString());
        Assert.Equal(auditBefore, (await ba.GetAsync($"/api/projects/{projectId}/ai-executions")).Body!.ToJsonString());
    }

    [Fact]
    public async Task Daily_token_limit_on_part_2_fails_at_once_naming_the_limit_and_saves_nothing()
    {
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(
            _ => GeminiDailyQuota(), r => PartOf(r) == 2 ? GroqDailyTokenLimit() : GroqOk(PartGeneration(PartOf(r))), DefaultMaxChunkedParts);
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(8_000, SystemPrompt));

        var response = await ba.Http.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements", null);
        var raw = await response.Content.ReadAsStringAsync();
        var problem = JsonNode.Parse(raw)!;

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("ai_rate_limited", problem["code"]!.GetValue<string>());
        Assert.Equal(2, problem["failedPart"]!.GetValue<int>());
        Assert.InRange(problem["partCount"]!.GetValue<int>(), 2, DefaultMaxChunkedParts);
        Assert.True(problem["quotaExhausted"]!.GetValue<bool>());
        Assert.Equal("TPD", problem["rateLimitType"]!.GetValue<string>());
        Assert.Contains("Groq reported that its tokens-per-day limit (TPD) was reached.", problem["detail"]!.GetValue<string>());
        Assert.StartsWith("Part 2 of ", problem["detail"]!.GetValue<string>());
        Assert.DoesNotContain(GroqOrganization, raw);

        // Part 1 once, part 2 once: no retry, no waiting out the hint, nothing after part 2. Gemini
        // only for the whole source; its daily quota then keeps it out of every part.
        Assert.Equal(["1", "2"], groqHttp.Bodies.Select(b => PartPattern.Match(GroqMessages(b).User).Groups[1].Value));
        Assert.Single(geminiHttp.Requests);

        Assert.Empty((await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray());
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.DoesNotContain(audit!.AsArray(), e => e!["operationType"]!.GetValue<string>() == "RequirementGeneration");
    }

    [Fact]
    public async Task Generate_requirements_endpoint_on_a_78k_token_source_merges_parts_after_gemini_quota()
    {
        var groqResponder = (Func<HttpRequestMessage, HttpResponseMessage>)(r => GroqOk(PartAnalysis(PartOf(r))));
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => groqResponder(r));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var source = ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens - 8_000, SystemPrompt);
        var (projectId, sourceId) = await CreateSourceAsync(ba, source);

        // Chunked analysis: five questions per part -> a clarification list larger than Groq's input budget.
        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);
        var analysisParts = groqHttp.Bodies.Count;
        var (_, questions) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/clarification-questions");
        Assert.Equal(analysisParts * 5, questions!.AsArray().Count);
        // Requirement generation lists each clarification with its code (P0-2): "- CQ-0xx Q: ...".
        var questionTexts = Enumerable.Range(1, analysisParts)
            .SelectMany(p => Enumerable.Range(1, 5).Select(k =>
                new Regex(@"- CQ-\d{3,} Q: " + Regex.Escape($"Which approver handles rule {k} of leave area {p} requests?\n"))))
            .ToList();

        groqResponder = r => GroqOk(PartGeneration(PartOf(r)));
        var (status, generated) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        var generationBodies = groqHttp.Bodies.Skip(analysisParts).ToList();
        var parts = generationBodies.Count;
        Assert.True(parts >= 20, $"expected >= 20 parts, got {parts}");
        Assert.All(generationBodies, AssertWithinGroqBudget);
        var users = generationBodies.Select(b => GroqMessages(b).User).ToList();
        Assert.Contains(users, u => u.Contains("Clarifications gathered so far:"));
        foreach (var question in questionTexts)
        {
            Assert.Contains(users, u => question.IsMatch(u));
        }

        // Merged: the restated FR/NFR once, every part's own FR kept, in source order.
        var frTitles = generated!["functionalRequirements"]!.AsArray().Select(f => f!["title"]!.GetValue<string>()).ToList();
        Assert.Equal(parts + 1, frTitles.Count);
        Assert.Equal("Request Leave", frTitles[0]);
        Assert.Equal(Enumerable.Range(1, parts).Select(p => $"Record leave area {p}"), frTitles.Skip(1));
        Assert.Single(generated["nonFunctionalRequirements"]!.AsArray());

        // Audit: one row per artifact, served by Groq, every part's raw response kept.
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        var rows = audit!.AsArray().Where(e => e!["operationType"]!.GetValue<string>() == "RequirementGeneration").ToList();
        Assert.Equal(parts + 2, rows.Count);
        Assert.All(rows, row => Assert.Equal("groq:openai/gpt-oss-120b", row!["model"]!.GetValue<string>()));
        Assert.Equal(parts, JsonNode.Parse(rows[0]!["outputJson"]!.GetValue<string>())!["parts"]!.AsArray().Count);
    }

    [Fact]
    public async Task Generate_requirements_endpoint_reports_the_failed_part_and_saves_nothing()
    {
        var (client, _, _) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r =>
            PartOf(r) == 3 ? GroqOk("not json at all") : GroqOk(PartGeneration(PartOf(r))));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(ReportedRequestTokens, SystemPrompt));

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", problem!["code"]!.GetValue<string>());
        Assert.Equal(3, problem["failedPart"]!.GetValue<int>());
        Assert.StartsWith("Part 3 of ", problem["detail"]!.GetValue<string>());
        Assert.Empty((await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray());
    }

    // ---- merge: distinct requirements kept, true duplicates combined ----------------------------

    private static JsonObject Fr(string title, string actor, string preconditions, string inputs, string processing, string expected,
        string[]? objectives = null, string[]? references = null) => new()
    {
        ["title"] = title, ["actor"] = actor, ["priority"] = "High",
        ["preconditions"] = preconditions, ["inputs"] = inputs, ["processing"] = processing, ["expectedResult"] = expected,
        ["dependencies"] = new JsonArray(),
        ["businessObjectiveCodes"] = new JsonArray((objectives ?? []).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
        ["sourceReferences"] = new JsonArray((references ?? []).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
    };

    private static JsonObject Nfr(string title, string category, string description, string? target) => new()
    {
        ["title"] = title, ["category"] = category, ["description"] = description, ["targetValue"] = target
    };

    private const string Excerpt = "meeting-notes.txt#0";

    // Part 1 states each requirement once; part 2 restates two of them (in different case and
    // punctuation - true duplicates) and adds requirements that share a title with part 1's but
    // differ in one content field each. Other parts add one requirement of their own.
    private static string MergeCase(int part) => part switch
    {
        1 => new JsonObject
        {
            ["functionalRequirements"] = new JsonArray(
                Fr("View report", "Manager", "Logged in", "Date range", "Build the leave report", "Leave totals are shown"),
                Fr("Approve leave", "Manager", "Request pending", "Decision", "Record the decision", "Employee is notified",
                    objectives: ["BO-001"], references: [Excerpt])),
            ["nonFunctionalRequirements"] = new JsonArray(
                Nfr("Response time", "Performance", "Search responds quickly", "2 seconds"),
                Nfr("Audit log", "Compliance", "Leave decisions are logged", null))
        }.ToJsonString(),
        2 => new JsonObject
        {
            ["functionalRequirements"] = new JsonArray(
                // True duplicate of part 1's "Approve leave", citing the other objective.
                Fr("approve leave.", "the Manager", "Request pending", "Decision", "Record the decision.", "Employee is notified",
                    objectives: ["BO-002"]),
                // Same title and actor as part 1's "View report", one field different each.
                Fr("View report", "Manager", "Logged in", "Date range", "Build the attendance report", "Leave totals are shown"),
                Fr("View report", "Manager", "Logged in", "Date range", "Build the leave report", "Leave totals are exported"),
                Fr("View report", "Manager", "Is an HR admin", "Date range", "Build the leave report", "Leave totals are shown"),
                Fr("View report", "Manager", "Logged in", "Team filter", "Build the leave report", "Leave totals are shown")),
            ["nonFunctionalRequirements"] = new JsonArray(
                Nfr("Response time", "Performance", "Search responds quickly", "2 seconds"),   // true duplicate
                Nfr("Response time", "Performance", "Search responds quickly", "10 seconds"),  // different target
                Nfr("Response time", "Performance", "Reports respond quickly", "2 seconds"),   // different description
                Nfr("Audit log", "Security", "Leave decisions are logged", null))              // different category
        }.ToJsonString(),
        _ => new JsonObject
        {
            ["functionalRequirements"] = new JsonArray(new JsonObject { ["title"] = $"Record leave area {part}", ["actor"] = "HR" }),
            ["nonFunctionalRequirements"] = new JsonArray()
        }.ToJsonString()
    };

    [Fact]
    public async Task Merge_keeps_requirements_that_share_a_title_but_differ_and_combines_true_duplicates()
    {
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(MergeCase(PartOf(r))));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (ps, project) = await ba.PostAsync("/api/projects", new
        {
            name = "Merge", description = "d", objectives = "Cut leave admin time\nKeep approvals auditable"
        });
        Assert.Equal(201, ps);
        var projectId = project!["id"]!.GetValue<string>();
        await UploadNotesAsync(ba, projectId);
        var (ss, created) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = ChunkedAnalysisTests.StructuredSource(12_000, SystemPrompt) });
        Assert.InRange(ss, 200, 201);

        var (status, generated) = await ba.PostAsync($"/api/requirement-sources/{created!["id"]!.GetValue<string>()}/generate-requirements");

        Assert.Equal(200, status);
        var parts = groqHttp.Bodies.Count;
        Assert.True(parts >= 3, $"expected >= 3 parts, got {parts}");
        Assert.All(groqHttp.Bodies, AssertWithinGroqBudget);
        Assert.All(groqHttp.Bodies, b => Assert.Contains($"[{Excerpt}]", GroqMessages(b).User)); // precondition: excerpt shown to every part

        // Read back what was saved, in code (= creation = source) order.
        var (_, artifacts) = await ba.GetAsync($"/api/projects/{projectId}/artifacts");
        var saved = artifacts!.AsArray().Select(a => a!.AsObject()).ToList();
        var frs = saved.Where(a => a["code"]!.GetValue<string>().StartsWith("FR-")).OrderBy(a => a["code"]!.GetValue<string>()).ToList();
        var nfrs = saved.Where(a => a["code"]!.GetValue<string>().StartsWith("NFR-")).OrderBy(a => a["code"]!.GetValue<string>()).ToList();

        // FRs: part 1's two, part 2's four variants of "View report" (its duplicate merged away), one per later part.
        Assert.Equal(2 + 4 + (parts - 2), frs.Count);
        Assert.Equal(generated!["functionalRequirements"]!.AsArray().Count, frs.Count);
        string Field(JsonObject a, string name) => a["data"]![name]!.GetValue<string>();
        Assert.Equal(["View report", "Approve leave", "View report", "View report", "View report", "View report"],
            frs.Take(6).Select(a => a["title"]!.GetValue<string>()));
        Assert.Equal(["Build the leave report", "Build the attendance report"], frs.Where((_, i) => i is 0 or 2).Select(a => Field(a, "processing")));
        Assert.Equal("Leave totals are exported", Field(frs[3], "expectedResult"));
        Assert.Equal("Is an HR admin", Field(frs[4], "preconditions"));
        Assert.Equal("Team filter", Field(frs[5], "inputs"));
        Assert.Equal(Enumerable.Range(3, parts - 2).Select(p => $"Record leave area {p}"), frs.Skip(6).Select(a => a["title"]!.GetValue<string>()));

        // The true duplicate is one FR carrying both parts' objectives and the excerpt reference.
        var approve = Assert.Single(frs, a => a["title"]!.GetValue<string>() == "Approve leave");
        Assert.Equal([Excerpt], approve["data"]!["sourceReferences"]!.AsArray().Select(r => r!.GetValue<string>()));
        var (_, links) = await ba.GetAsync($"/api/artifacts/{approve["id"]!.GetValue<string>()}/relationships");
        Assert.Equal(["BO-001", "BO-002"], links!.AsArray()
            .Where(l => l!["isOutgoing"]!.GetValue<bool>()).Select(l => l!["relatedArtifactCode"]!.GetValue<string>()).Order());

        // NFRs: the exact restatement merged; different target, description or category all kept.
        Assert.Equal(2 + 3, nfrs.Count);
        var responseTimes = nfrs.Where(a => a["title"]!.GetValue<string>() == "Response time")
            .Select(a => (Field(a, "description"), Field(a, "targetValue"))).ToList();
        Assert.Equal([("Search responds quickly", "2 seconds"), ("Search responds quickly", "10 seconds"), ("Reports respond quickly", "2 seconds")],
            responseTimes);
        Assert.Equal(["Compliance", "Security"], nfrs.Where(a => a["title"]!.GetValue<string>() == "Audit log").Select(a => Field(a, "category")));
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

    // ---- a split that yields a single part ------------------------------------------------------

    // The reviewed scenario - a short source with many clarifications - never produces one part on
    // the real stack: either every clarification fits next to the source (so the whole prompt
    // fitted and was sent whole) or some fit no part and nothing is sent. Pinned here so a change
    // to the budget arithmetic that makes it reachable is noticed.
    [Fact]
    public async Task Short_source_with_many_clarifications_on_groq_is_refused_clearly_and_saves_nothing()
    {
        var analysis = new JsonObject
        {
            ["actors"] = new JsonArray("Employee"), ["capabilities"] = new JsonArray("Request leave"),
            ["dataElements"] = new JsonArray(), ["notes"] = new JsonArray(),
            ["missingInformation"] = new JsonArray(Enumerable.Range(1, 60).Select(k => (JsonNode)new JsonObject
            {
                ["topic"] = $"Rule {k}", ["question"] = $"Which approver handles leave rule {k} when the manager is away?", ["reason"] = "Not stated"
            }).ToArray())
        }.ToJsonString();
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), _ => GroqOk(analysis));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(1_500, SystemPrompt));
        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);
        var analysisRequests = groqHttp.Bodies.Count;
        var questions = (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray().Count;
        Assert.Equal(60, questions);

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", problem!["code"]!.GetValue<string>());
        Assert.Contains("of its 60 clarifications do not fit", problem["detail"]!.GetValue<string>());
        Assert.Equal(analysisRequests, groqHttp.Bodies.Count); // no generation request reached Groq
        Assert.Equal(questions, (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray().Count);
    }

    // A provider whose own budget check refuses the whole request although our estimate says the
    // source fits one part (the only way to get a single part): that part is the whole source and
    // must meet the whole-source rules - here "at least one functional requirement" - with its
    // single repair attempt, instead of being accepted empty.
    private sealed class RefusesWholeRequestClient : IUncondensedAiChatClient
    {
        public List<string> PartPrompts { get; } = new();
        public string ModelName => "groq:openai/gpt-oss-120b";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
            CompleteUncondensedAsync(systemPrompt, userPrompt, cancellationToken);

        public Task<string> CompleteUncondensedAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            if (!userPrompt.Contains("This is part 1 of 1."))
            {
                throw new AiRequestOverBudgetException("Groq", InputBudget, InputBudget + 1);
            }

            PartPrompts.Add(userPrompt);
            return Task.FromResult("{\"functionalRequirements\":[],\"nonFunctionalRequirements\":[]}");
        }
    }

    [Fact]
    public async Task Single_part_returning_no_functional_requirements_fails_clearly_after_one_repair_and_saves_nothing()
    {
        var client = new RefusesWholeRequestClient();
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(1_500, SystemPrompt));

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", problem!["code"]!.GetValue<string>());
        Assert.Equal(1, problem["failedPart"]!.GetValue<int>());
        Assert.Equal(1, problem["partCount"]!.GetValue<int>());
        Assert.StartsWith("Part 1 of 1 ", problem["detail"]!.GetValue<string>());
        Assert.Contains(problem["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("functionalRequirements must contain at least 1 item(s)"));
        Assert.Equal(2, client.PartPrompts.Count); // the part and its one repair attempt
        Assert.Contains("Your previous response was rejected by automatic validation", client.PartPrompts[1]);
        Assert.Empty((await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray());
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.DoesNotContain(audit!.AsArray(), e => e!["operationType"]!.GetValue<string>() == "RequirementGeneration");
    }

    private static async Task<(string ProjectId, string SourceId)> CreateSourceAsync(ApiUser ba, string rawText)
    {
        var (ps, project) = await ba.PostAsync("/api/projects", new { name = "Chunked generation", description = "d" });
        Assert.Equal(201, ps);
        var projectId = project!["id"]!.GetValue<string>();
        var (ss, created) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText });
        Assert.InRange(ss, 200, 201);
        return (projectId, created!["id"]!.GetValue<string>());
    }
}
