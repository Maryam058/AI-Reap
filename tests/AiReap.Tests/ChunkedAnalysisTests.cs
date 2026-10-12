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

// Requirement analysis of a source too large for Groq's per-request budget (Ai:Groq:MaxRequestTokens
// = 8000): instead of condensing it, GenerationSupport.CallAiAndParseChunkedAsync splits the source
// at logical boundaries into parts that each fit, and RequirementAnalysisService merges the parts.
// Gemini stays primary and receives the whole source unchanged. Real Gemini/Groq clients over fake
// HTTP (GroqRequestBudgetTests.BuildStack) - never a live API.
public class ChunkedAnalysisTests
{
    private const int MaxRequestTokens = 8000;
    private const int ReservedOutputTokens = 4000;
    private const int InputBudget = MaxRequestTokens - ReservedOutputTokens;

    private const string SystemPrompt = """
        You are a requirements analyst. Respond with ONLY a single JSON object matching exactly this shape:
        {"actors": ["string"], "capabilities": ["string"], "dataElements": ["string"], "notes": ["string"],
         "missingInformation": [{"topic": "string", "question": "string", "reason": "string"}]}
        """;

    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");

    // ---- sources ---------------------------------------------------------------------------

    // A structured requirements document: numbered section headings, an intro paragraph, a bullet
    // list and numbered requirement statements (REQ-s.k, each unique), padded with a closing
    // paragraph so that the whole analysis request is estimated at exactly `requestTokens`.
    internal static string StructuredSource(int requestTokens, string systemPrompt = SystemPrompt)
    {
        var chars = (int)((requestTokens - PromptBudgetReducer.RequestOverheadTokens - PromptBudgetReducer.EstimateTokens(systemPrompt))
            * PromptBudgetReducer.CharsPerToken);
        var sb = new StringBuilder("Leave Management System - Requirements Specification");
        for (var s = 1; ; s++)
        {
            var section = Section(s);
            if (sb.Length + section.Length + 400 > chars)
            {
                break;
            }

            sb.Append("\n\n").Append(section);
        }

        sb.Append("\n\nClosing remarks: ");
        while (sb.Length < chars)
        {
            sb.Append("the project board reviews this specification every quarter and records decisions ");
        }

        sb.Length = chars;
        var text = sb.ToString().TrimEnd();
        return text + new string('.', chars - text.Length);
    }

    private static string Section(int s) =>
        $"## {s}. Leave area {s}\n\n" +
        $"This section describes how leave area {s} works for employees, managers and HR staff in every office.\n\n" +
        $"- Employees see their balance for area {s}.\n- Managers see their team calendar for area {s}.\n- HR sees the audit history for area {s}.\n\n" +
        string.Join("\n", Enumerable.Range(1, 6).Select(k =>
            $"{k}. REQ-{s}.{k}: The system must record every leave request for area {s} with its dates, type and approver, and keep it for seven years."));

    private static IEnumerable<string> RequirementIds(string source) =>
        Regex.Matches(source, @"REQ-\d+\.\d+").Select(m => m.Value);

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

    private static HttpResponseMessage GroqRateLimited() =>
        WithRetryAfter(GroqAiChatClientTests.Json(new
        {
            error = new
            {
                message = "Rate limit reached for model `openai/gpt-oss-120b` on tokens per minute (TPM): Limit 8000, Used 7900, Requested 5500. Please try again in 40s.",
                type = "tokens",
                code = "rate_limit_exceeded"
            }
        }, HttpStatusCode.TooManyRequests), 40);

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, int seconds)
    {
        response.Headers.Add("retry-after", seconds.ToString());
        return response;
    }

    private const string SmallAnalysis =
        "{\"actors\":[\"Employee\"],\"capabilities\":[\"Request leave\"],\"dataElements\":[],\"notes\":[],\"missingInformation\":[]}";

    // Every part names the shared actors (in varying wording) plus one of its own, and asks the
    // same carry-over question; odd parts also ask a part-specific question.
    private static string PartAnalysis(int part) =>
        new JsonObject
        {
            ["actors"] = new JsonArray(part % 2 == 0 ? "Employees" : "Employee", part % 2 == 0 ? "the manager" : "Manager", $"Auditor {part}"),
            ["capabilities"] = new JsonArray("Request leave", part % 2 == 0 ? "Request Leave." : "Approve leave", $"Capability {part}"),
            ["dataElements"] = new JsonArray("Leave balance", $"Area {part} record"),
            ["notes"] = new JsonArray($"Note from part {part}"),
            ["missingInformation"] = part % 2 == 1
                ? new JsonArray(
                    new JsonObject { ["topic"] = "Carry-over", ["question"] = "Can unused leave carry over to next year?", ["reason"] = part == 1 ? "" : "Not stated" },
                    new JsonObject { ["topic"] = $"Area {part} retention", ["question"] = $"Who may delete area {part} records?", ["reason"] = "Not stated" })
                : new JsonArray(
                    new JsonObject { ["topic"] = "carry over", ["question"] = "Can unused leave carry over to the next year?", ["reason"] = "Unclear" })
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

    // What reaches Groq's HTTP endpoint: estimated input within the input budget, and estimated
    // input + max_completion_tokens within Ai:Groq:MaxRequestTokens.
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
        public List<string> Actors { get; set; } = new();
        public List<string> Capabilities { get; set; } = new();
        public virtual void Validate(AiResponseValidator v) => v.NoBlankEntries(Actors, "actors");
    }

    public sealed class WholeShape : PartShape
    {
        public override void Validate(AiResponseValidator v)
        {
            base.Validate(v);
            if (Actors.Count == 0) v.Fail("No actors.");
        }
    }

    private static Task<IReadOnlyList<GenerationSupport.AiPart<PartShape>>> Chunked(
        IAiChatClient client, string source, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        GenerationSupport.CallAiAndParseChunkedAsync<WholeShape, PartShape>(
            client, NullLogger.Instance, "RequirementAnalysis", "src-1", SystemPrompt, source, CancellationToken.None,
            delay: delay ?? ((_, _) => Task.CompletedTask));

    // ---- provider routing ------------------------------------------------------------------

    [Fact]
    public async Task Small_request_served_by_gemini_is_one_unchanged_request()
    {
        const string source = "Employees request leave; managers approve it.";
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiOk(SmallAnalysis), _ => GroqOk(SmallAnalysis));

        var parts = await Chunked(client, source);

        var part = Assert.Single(parts);
        Assert.Equal((1, 1), (part.Number, part.Count));
        Assert.Equal("gemini-3.8-flash", part.Model);
        var sent = JsonNode.Parse(Assert.Single(geminiHttp.Bodies))!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(source, sent);
        Assert.Empty(groqHttp.Requests);
    }

    [Fact]
    public async Task Small_request_served_by_groq_is_one_unchanged_request()
    {
        const string source = "Employees request leave; managers approve it.";
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), _ => GroqOk(SmallAnalysis));

        var parts = await Chunked(client, source);

        var part = Assert.Single(parts);
        Assert.Equal("groq:openai/gpt-oss-120b", part.Model);
        Assert.Single(geminiHttp.Requests); // primary tried first
        var (system, user) = GroqMessages(Assert.Single(groqHttp.Bodies));
        Assert.Equal(SystemPrompt, system);
        Assert.Equal(source, user); // no part header, no condensing
    }

    [Fact]
    public async Task Large_request_served_by_gemini_is_still_one_whole_request()
    {
        var source = StructuredSource(23_580);
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiOk(SmallAnalysis), _ => GroqOk(SmallAnalysis));

        var parts = await Chunked(client, source);

        Assert.Single(parts);
        var sent = JsonNode.Parse(Assert.Single(geminiHttp.Bodies))!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(source, sent);
        Assert.Empty(groqHttp.Requests);
        Assert.Equal("gemini-3.8-flash", client.ModelName);
    }

    // The reported failure: "needs about 23,580" tokens, Gemini quota used up, Groq fallback.
    [Fact]
    public async Task Source_of_about_23580_tokens_on_groq_is_split_into_several_requests_each_within_the_budget()
    {
        var source = StructuredSource(23_580);
        Assert.Equal(23_580, PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, source));
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(PartAnalysis(PartOf(r))));

        var parts = await Chunked(client, source);

        Assert.InRange(parts.Count, 4, 12);
        Assert.Equal(Enumerable.Range(1, parts.Count), parts.Select(p => p.Number));
        Assert.All(parts, p => Assert.Equal("groq:openai/gpt-oss-120b", p.Model));

        // Gemini stays primary: it is tried first for the whole source and again for every part
        // (this 429 carries no retry hint, so there is no cool-down), and each falls back to Groq.
        Assert.Equal(parts.Count + 1, geminiHttp.Requests.Count);
        Assert.Equal(parts.Count, groqHttp.Bodies.Count);

        var users = new List<string>();
        foreach (var (body, index) in groqHttp.Bodies.Select((b, i) => (b, i)))
        {
            AssertWithinGroqBudget(body);
            var (system, user) = GroqMessages(body);
            Assert.Equal(SystemPrompt, system); // instructions/schema in every part
            Assert.StartsWith($"[AI-REAP note:", user);
            Assert.Contains($"This is part {index + 1} of {parts.Count}.", user);
            Assert.DoesNotContain("omitted", user); // never condensed
            users.Add(user);
        }

        // Nothing dropped, nothing duplicated: every requirement statement is in exactly one part.
        foreach (var id in RequirementIds(source))
        {
            Assert.Equal(1, users.Count(u => Regex.IsMatch(u, Regex.Escape(id) + @"\b")));
        }

        Assert.Contains("Leave Management System - Requirements Specification", users[0]);
        Assert.Contains("Closing remarks:", users[^1]);
    }

    [Fact]
    public async Task Groq_only_configuration_is_chunked_the_same_way()
    {
        var source = StructuredSource(23_580);
        var groqHttp = new GroqAiChatClientTests.FakeHandler(r => GroqOk(PartAnalysis(PartOf(r))));
        var groq = new GroqAiChatClient(new HttpClient(groqHttp), Options.Create(new GroqOptions
        {
            ApiKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST"
        }));
        var client = new RequestBudgetAiChatClient(
            new ResilientAiChatClient(groq, "Groq", new AiResilienceOptions { BaseDelayMilliseconds = 0 }, NullLogger.Instance, (_, _) => Task.CompletedTask),
            "Groq", "Ai:Groq:MaxRequestTokens", MaxRequestTokens, ReservedOutputTokens, NullLogger.Instance);

        var parts = await Chunked(client, source);

        Assert.True(parts.Count > 1);
        Assert.Equal(parts.Count, groqHttp.Bodies.Count);
        Assert.All(groqHttp.Bodies, AssertWithinGroqBudget);
    }

    [Fact]
    public async Task Uncondensed_call_that_does_not_fit_groq_is_refused_before_any_groq_request()
    {
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), _ => GroqOk(SmallAnalysis));

        var ex = await Assert.ThrowsAsync<AiRequestOverBudgetException>(
            () => client.CompleteUncondensedAsync(SystemPrompt, StructuredSource(23_580)));

        Assert.Equal("Groq", ex.Provider);
        Assert.Equal(InputBudget, ex.InputBudget);
        Assert.Equal(23_580, ex.EstimatedTokens);
        Assert.Empty(groqHttp.Requests);
    }

    // ---- repair, rate limits, failures -------------------------------------------------------

    [Fact]
    public async Task A_part_with_an_invalid_response_gets_one_repair_attempt_of_its_own()
    {
        var source = StructuredSource(23_580);
        var part2Calls = 0;
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r =>
        {
            var part = PartOf(r);
            return part == 2 && ++part2Calls == 1 ? GroqOk("{\"actors\":[\"  \"]}") : GroqOk(PartAnalysis(part));
        });

        var parts = await Chunked(client, source);

        Assert.Equal(parts.Count + 1, groqHttp.Bodies.Count); // one extra call: the repair of part 2
        Assert.Equal(2, part2Calls);
        var repair = groqHttp.Bodies[2];
        AssertWithinGroqBudget(repair);
        var (_, user) = GroqMessages(repair);
        Assert.Contains("This is part 2 of", user);
        Assert.Contains("Your previous response was rejected by automatic validation", user);
        Assert.Equal(1, groqHttp.Bodies.Count(b => GroqMessages(b).User.Contains("This is part 1 of"))); // earlier parts not re-sent
    }

    [Fact]
    public async Task A_part_that_hits_the_per_minute_limit_waits_for_the_hint_and_is_sent_again()
    {
        var source = StructuredSource(23_580);
        var part2Calls = 0;
        var waits = new List<TimeSpan>();
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r =>
        {
            var part = PartOf(r);
            // 40s is beyond ResilientAiChatClient's 30s retry limit, so only the part-level wait re-sends it.
            return part == 2 && ++part2Calls == 1 ? GroqRateLimited() : GroqOk(PartAnalysis(part));
        });

        var parts = await Chunked(client, source, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        Assert.True(parts.Count > 2);
        Assert.Contains(TimeSpan.FromSeconds(40), waits);
        Assert.Equal(2, part2Calls);
        Assert.Equal(1, groqHttp.Bodies.Count(b => GroqMessages(b).User.Contains("This is part 1 of")));
    }

    [Fact]
    public async Task A_part_that_keeps_failing_validation_fails_the_whole_analysis_naming_the_part()
    {
        var (client, _, _) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r =>
            PartOf(r) == 3 ? GroqOk("{\"actors\":[\"  \"]}") : GroqOk(PartAnalysis(PartOf(r))));

        var ex = await Assert.ThrowsAsync<AiOutputValidationException>(() => Chunked(client, StructuredSource(23_580)));

        Assert.StartsWith("Part 3 of ", ex.Message);
        Assert.All(ex.Errors, e => Assert.StartsWith("Part 3 of ", e));
        Assert.Equal(3, ex.Data[GenerationSupport.FailedPartKey]);
    }

    [Fact]
    public async Task A_part_whose_provider_call_fails_fails_the_whole_analysis_naming_the_part()
    {
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r =>
            PartOf(r) == 2
                ? GroqAiChatClientTests.Json(new { error = new { message = "Internal error", type = "server_error" } }, HttpStatusCode.InternalServerError)
                : GroqOk(PartAnalysis(PartOf(r))));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Chunked(client, StructuredSource(23_580)));

        Assert.Equal(AiFailureKind.Unavailable, ex.Kind);
        Assert.StartsWith("Part 2 of ", ex.Message);
        Assert.Contains("Nothing was saved", ex.Message);
        Assert.Equal(2, ex.Data[GenerationSupport.FailedPartKey]);
        Assert.DoesNotContain(groqHttp.Bodies, b => GroqMessages(b).User.Contains("This is part 3 of")); // stopped at the failure
    }

    [Fact]
    public async Task Fixed_parts_that_leave_no_room_for_source_text_are_refused_without_calling_groq()
    {
        var hugeSystem = SystemPrompt + new string('x', 17_000); // ~5,700 tokens of instructions
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), _ => GroqOk(SmallAnalysis));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => GenerationSupport.CallAiAndParseChunkedAsync<WholeShape, PartShape>(
            client, NullLogger.Instance, "RequirementAnalysis", "src-1", hugeSystem, StructuredSource(23_580), CancellationToken.None));

        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("cannot be split", ex.Message);
        Assert.Empty(groqHttp.Requests);
    }

    // ---- through the API: merge, dedupe, persistence, audit ---------------------------------

    [Fact]
    public async Task Analyze_endpoint_merges_the_parts_and_removes_duplicates()
    {
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(PartAnalysis(PartOf(r))));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, StructuredSource(23_580));

        var (status, analysis) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");

        Assert.Equal(200, status);
        var partCount = groqHttp.Bodies.Count;
        Assert.True(partCount >= 4);
        Assert.All(groqHttp.Bodies, AssertWithinGroqBudget);

        // Shared actors reported in different wording by each part appear once, first wording kept.
        var actors = Strings(analysis!["actors"]);
        Assert.Equal(["Employee", "Manager"], actors.Take(2));
        Assert.Equal(partCount + 2, actors.Count);
        Assert.Equal(Enumerable.Range(1, partCount).Select(p => $"Auditor {p}"), actors.Skip(2));

        var capabilities = Strings(analysis["capabilities"]);
        Assert.Equal(1, capabilities.Count(c => AiResultMerge.Key(c) == "request leave"));
        Assert.Contains("Approve leave", capabilities);
        Assert.Equal(1, Strings(analysis["dataElements"]).Count(d => d == "Leave balance"));
        Assert.Equal(partCount, Strings(analysis["notes"]).Count);

        // The carry-over question asked by every part becomes one clarification question (with a
        // reason taken from a later duplicate); part-specific questions are all kept.
        var questions = analysis["clarificationQuestions"]!.AsArray();
        var oddParts = Enumerable.Range(1, partCount).Count(p => p % 2 == 1);
        Assert.Equal(1 + oddParts, questions.Count);
        Assert.Equal("Carry-over", questions[0]!["title"]!.GetValue<string>());
        Assert.Equal(questions.Count, (await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray().Count);

        // Audit: served by Groq, every part's raw response kept.
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        var rows = audit!.AsArray().Where(e => e!["operationType"]!.GetValue<string>() == "RequirementAnalysis").ToList();
        Assert.Equal(questions.Count, rows.Count);
        Assert.All(rows, row => Assert.Equal("groq:openai/gpt-oss-120b", row!["model"]!.GetValue<string>()));
        var output = JsonNode.Parse(rows[0]!["outputJson"]!.GetValue<string>())!["parts"]!.AsArray();
        Assert.Equal(partCount, output.Count);
        Assert.Equal(partCount, output[^1]!["of"]!.GetValue<int>());
    }

    [Fact]
    public async Task Analyze_endpoint_reports_the_failed_part_and_saves_nothing()
    {
        var (client, _, _) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r =>
            PartOf(r) == 2 ? GroqOk("not json at all") : GroqOk(PartAnalysis(PartOf(r))));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, StructuredSource(23_580));

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", problem!["code"]!.GetValue<string>());
        Assert.Equal(2, problem["failedPart"]!.GetValue<int>());
        Assert.True(problem["partCount"]!.GetValue<int>() >= 4);
        Assert.StartsWith("Part 2 of ", problem["detail"]!.GetValue<string>());
        Assert.Empty((await ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray());
    }

    [Fact]
    public async Task Analyze_endpoint_with_gemini_available_sends_the_whole_large_source_once()
    {
        var geminiAnalysis = PartAnalysis(1);
        var (client, geminiHttp, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiOk(geminiAnalysis), _ => GroqOk(SmallAnalysis));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var source = StructuredSource(23_580);
        var (projectId, sourceId) = await CreateSourceAsync(ba, source);

        var (status, _) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");

        Assert.Equal(200, status);
        var sent = JsonNode.Parse(Assert.Single(geminiHttp.Bodies))!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(source, sent);
        Assert.Empty(groqHttp.Requests);
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        var row = audit!.AsArray().First(e => e!["operationType"]!.GetValue<string>() == "RequirementAnalysis");
        Assert.Equal("gemini-3.8-flash", row!["model"]!.GetValue<string>());
        Assert.Equal(geminiAnalysis, row["outputJson"]!.GetValue<string>()); // raw response, as before
    }

    private static async Task<(string ProjectId, string SourceId)> CreateSourceAsync(ApiUser ba, string rawText)
    {
        var (ps, project) = await ba.PostAsync("/api/projects", new { name = "Chunked", description = "d" });
        Assert.Equal(201, ps);
        var projectId = project!["id"]!.GetValue<string>();
        var (ss, source) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText });
        Assert.InRange(ss, 200, 201);
        return (projectId, source!["id"]!.GetValue<string>());
    }

    private static List<string> Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToList();
}
