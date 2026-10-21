using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AiReap.Tests.GroundedGenerationTests;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// P0-7 — Analyze and Generate results carry an optional, response-only `completeness`: whether the
// source went whole or in parts (every passage once), how the clarifications travelled, the models
// that served it, and - for Generate - evidence warning counts over the requirements the run created.
// Coverage stays in `sourceIdCoverage` (P0-4); evidence comes from the saved P0-2 fields. Scripted AI
// (the test host's stub) or real Gemini/Groq clients over fake HTTP - never a live API; throwaway
// test databases.
public class CompletenessFlagsTests
{
    private const int Fr = 0, Nfr = 1;
    private const string GenerationMarker = "requirementOrigin";
    private const string GroqModel = "groq:openai/gpt-oss-120b";

    private static readonly string[] CompletenessKeys =
        ["contextReduction", "partCount", "clarifications", "models", "unverifiedEvidenceCount", "noEvidenceCount", "unsupportedNumbersCount"];

    private static JsonObject Cite(JsonObject item, params string[] ids)
    {
        item["sourceIds"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        return item;
    }

    // Every FR/NFR of Srs cited with a quote found as written, no unsupported number.
    private static string Grounded() => Response(
        [
            Cite(FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", "Employees must be able to submit a leave request online", "FR-001"))), "FR-001"),
            Cite(FrItem("Approve leave", "explicit", new JsonArray(Quote("srs", "Managers must approve leave requests within 2 days", "FR-002"))), "FR-002")
        ],
        [Cite(NfrItem("Leave page speed", "3 seconds", "stated", new JsonArray(Quote("srs", "The leave page must load within 3 seconds for 95% of requests", "NFR-001"))), "NFR-001")]);

    // One quote found, one altered quote, one requirement with no quote, and an NFR whose target
    // ("2 seconds") is in none of its quotes.
    private const string AlteredQuote = "Managers must approve leave requests within 5 days";

    private static string Mixed() => Response(
        [
            Cite(FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", "Employees must be able to submit a leave request online", "FR-001"))), "FR-001"),
            Cite(FrItem("Approve leave", "explicit", new JsonArray(Quote("srs", AlteredQuote, "FR-002"))), "FR-002"),
            FrItem("Notify employee", "derived", null)
        ],
        [Cite(NfrItem("Leave page speed", "2 seconds", "stated", new JsonArray(Quote("srs", "The leave page must load within 3 seconds", "NFR-001"))), "NFR-001")]);

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static async Task<string> AuditedModelAsync(ApiUser ba, string projectId, string operation)
    {
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        return audit!.AsArray().First(e => e!["operationType"]!.GetValue<string>() == operation)!["model"]!.GetValue<string>();
    }

    // ---- whole-source success, and the JSON contract ------------------------------------------

    [Fact]
    public async Task Whole_source_run_reports_no_reduction_whole_clarifications_its_model_and_zero_evidence_warnings()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);

        var (analyzeStatus, analysis) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, analyzeStatus);
        // Analyze: existing fields unchanged and in order; completeness appended, without evidence counts.
        Assert.Equal(["actors", "capabilities", "dataElements", "notes", "clarificationQuestions", "skippedDuplicates", "completeness"],
            analysis!.AsObject().Select(kv => kv.Key));
        var analyzed = analysis["completeness"]!;
        Assert.Equal(CompletenessKeys, analyzed.AsObject().Select(kv => kv.Key));
        Assert.Equal("none", analyzed["contextReduction"]!.GetValue<string>());
        Assert.Equal(1, analyzed["partCount"]!.GetValue<int>());
        Assert.Equal("none", analyzed["clarifications"]!.GetValue<string>());
        Assert.Equal([await AuditedModelAsync(ba, projectId, "RequirementAnalysis")], Strings(analyzed["models"]));
        Assert.Null(analyzed["unverifiedEvidenceCount"]);
        Assert.Null(analyzed["noEvidenceCount"]);
        Assert.Null(analyzed["unsupportedNumbersCount"]);

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Grounded());
        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.Equal(["functionalRequirements", "nonFunctionalRequirements", "skippedDuplicates", "sourceIdCoverage", "completeness"],
            result!.AsObject().Select(kv => kv.Key));
        Assert.Equal(2, result["functionalRequirements"]!.AsArray().Count);
        Assert.Single(result["nonFunctionalRequirements"]!.AsArray());
        Assert.Equal("complete", result["sourceIdCoverage"]!["status"]!.GetValue<string>());

        var completeness = result["completeness"]!;
        Assert.Equal(CompletenessKeys, completeness.AsObject().Select(kv => kv.Key));
        Assert.Equal("none", completeness["contextReduction"]!.GetValue<string>());
        Assert.Equal(1, completeness["partCount"]!.GetValue<int>());
        Assert.Equal("whole", completeness["clarifications"]!.GetValue<string>()); // the two analysis questions went with the request
        Assert.Equal([await AuditedModelAsync(ba, projectId, "RequirementGeneration")], Strings(completeness["models"]));
        Assert.Equal(0, completeness["unverifiedEvidenceCount"]!.GetValue<int>());
        Assert.Equal(0, completeness["noEvidenceCount"]!.GetValue<int>());
        Assert.Equal(0, completeness["unsupportedNumbersCount"]!.GetValue<int>());
    }

    // ---- evidence counts: this run's created requirements, from their saved P0-2 fields ----------

    [Fact]
    public async Task Evidence_counts_match_the_saved_evidence_of_the_created_requirements_and_carry_no_quote_or_source_text()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Mixed());

        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        var completeness = result!["completeness"]!;
        Assert.Equal("none", completeness["clarifications"]!.GetValue<string>()); // not analysed: no clarifications
        Assert.Equal(1, completeness["unverifiedEvidenceCount"]!.GetValue<int>());
        Assert.Equal(1, completeness["noEvidenceCount"]!.GetValue<int>());
        Assert.Equal(1, completeness["unsupportedNumbersCount"]!.GetValue<int>());

        // The same numbers the saved artifacts give (no second evidence check).
        var saved = (await ArtifactsAsync(ba, projectId, Fr)).Concat(await ArtifactsAsync(ba, projectId, Nfr)).ToList();
        Assert.Equal(4, saved.Count);
        Assert.Equal(1, saved.Count(a => a!["data"]!["evidenceStatus"]?.GetValue<string>() == "unverified"));
        Assert.Equal(1, saved.Count(a => a!["data"]!["evidenceStatus"]?.GetValue<string>() == "none"));
        Assert.Equal(1, saved.Count(a => a!["data"]!["unsupportedNumbers"]!.AsArray().Count > 0));

        var json = completeness.ToJsonString();
        Assert.DoesNotContain("Managers must approve", json);
        Assert.DoesNotContain("Employees must be able", json);
        Assert.DoesNotContain("seconds", json);
        // Coverage is not repeated inside completeness.
        Assert.DoesNotContain("FR-00", json);
    }

    [Fact]
    public async Task Rerun_that_only_finds_duplicates_counts_no_evidence_and_still_reports_coverage()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (_, sourceId) = await SourceAsync(ba);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Mixed());
        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Mixed());
        var (status, rerun) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.Empty(rerun!["functionalRequirements"]!.AsArray());
        Assert.Empty(rerun["nonFunctionalRequirements"]!.AsArray());
        Assert.Equal(4, rerun["skippedDuplicates"]!.GetValue<int>());
        var completeness = rerun["completeness"]!;
        Assert.Equal(0, completeness["unverifiedEvidenceCount"]!.GetValue<int>());
        Assert.Equal(0, completeness["noEvidenceCount"]!.GetValue<int>());
        Assert.Equal(0, completeness["unsupportedNumbersCount"]!.GetValue<int>());
        Assert.Equal("complete", rerun["sourceIdCoverage"]!["status"]!.GetValue<string>()); // counted over every saved requirement
    }

    // ---- split processing on Groq ----------------------------------------------------------------

    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");

    private static int PartOf(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var user = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
        var match = PartPattern.Match(user);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static HttpResponseMessage GroqOk(string content) => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content));

    private static HttpResponseMessage GeminiQuota() => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new { error = new { code = 429, message = "You exceeded your current quota (per day).", status = "RESOURCE_EXHAUSTED" } })
    };

    // A generation part: one requirement of its own, without evidence.
    private static string PartGeneration(int part) => Response([FrItem($"Record leave area {part}", "derived", null)]);

    private static string PartAnalysis() => new JsonObject
    {
        ["actors"] = new JsonArray("Employee"), ["capabilities"] = new JsonArray("Request leave"),
        ["dataElements"] = new JsonArray(), ["notes"] = new JsonArray(), ["missingInformation"] = new JsonArray()
    }.ToJsonString();

    [Fact]
    public async Task Source_split_on_groq_reports_every_part_and_the_groq_model()
    {
        var analyzing = true;
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(analyzing ? PartAnalysis() : PartGeneration(PartOf(r))));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (_, sourceId) = await SourceAsync(ba, ChunkedAnalysisTests.StructuredSource(12_000));

        var (analyzeStatus, analysis) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, analyzeStatus);
        var analysisParts = groqHttp.Bodies.Count;
        Assert.True(analysisParts > 1, $"expected a split analysis, got {analysisParts} part(s)");
        var analyzed = analysis!["completeness"]!;
        Assert.Equal("split", analyzed["contextReduction"]!.GetValue<string>());
        Assert.Equal(analysisParts, analyzed["partCount"]!.GetValue<int>());
        Assert.Equal([GroqModel], Strings(analyzed["models"]));

        analyzing = false;
        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        var parts = groqHttp.Bodies.Count - analysisParts;
        Assert.True(parts > 1);
        var completeness = result!["completeness"]!;
        Assert.Equal("split", completeness["contextReduction"]!.GetValue<string>());
        Assert.Equal(parts, completeness["partCount"]!.GetValue<int>());
        Assert.Equal("none", completeness["clarifications"]!.GetValue<string>()); // the analysis found no questions
        Assert.Equal([GroqModel], Strings(completeness["models"]));
        Assert.Equal(parts, result["functionalRequirements"]!.AsArray().Count);
        Assert.Equal(parts, completeness["noEvidenceCount"]!.GetValue<int>());
        Assert.Equal(0, completeness["unverifiedEvidenceCount"]!.GetValue<int>());
        // The "1. REQ-n.k:" lines define IDs (P0-4) that the scripted parts never cite.
        Assert.Equal("incomplete", result["sourceIdCoverage"]!["status"]!.GetValue<string>());
    }

    // Clarifications on a split source: whole with every part when they fit, otherwise distributed
    // (each part the most relevant ones, every one with at least one part).
    [Theory]
    [InlineData(3, "whole")]
    [InlineData(120, "distributed")]
    public async Task Split_source_reports_whether_each_part_carried_every_clarification(int clarifications, string expected)
    {
        var shared = new GenerationSupport.SharedContext(ClarificationContextBuilder.Heading,
            Enumerable.Range(1, clarifications).Select(i =>
                $"- Q: Who approves leave requests for leave area {1 + (i - 1) % 40} when the approver of case {i} is absent?\n" +
                "  A: (not yet answered - treat as still unresolved, propose an assumption if needed and flag it)\n").ToList());
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(),
            _ => GroqOk("{\"functionalRequirements\":[{\"title\":\"Record leave\",\"actor\":\"HR\"}]}"));

        var parts = await GenerationSupport.CallAiAndParseChunkedAsync<ChunkedRequirementGenerationTests.WholeShape, ChunkedRequirementGenerationTests.PartShape>(
            client, NullLogger.Instance, "RequirementGeneration", "src-1", "Return JSON.", ChunkedAnalysisTests.StructuredSource(20_000),
            CancellationToken.None, delay: (_, _) => Task.CompletedTask, sharedContext: shared);

        Assert.True(parts.Count > 1);
        var completeness = RequirementCompleteness.ForParts(parts, shared.Items.Count);
        Assert.Equal("split", completeness.ContextReduction);
        Assert.Equal(groqHttp.Bodies.Count, completeness.PartCount);
        Assert.Equal(expected, completeness.Clarifications);
        Assert.Equal([GroqModel], completeness.Models);
    }

    [Fact]
    public void Models_are_listed_once_each_and_none_is_inferred_when_unreported()
    {
        GenerationSupport.AiPart<object> Part(int n, string model) => new(n, 3, "{}", new object(), model);

        var mixed = RequirementCompleteness.ForParts([Part(1, "gemini:x"), Part(2, GroqModel), Part(3, "gemini:x")], 0);
        Assert.Equal(["gemini:x", GroqModel], mixed.Models);

        var unreported = RequirementCompleteness.ForParts([new GenerationSupport.AiPart<object>(1, 1, "{}", new object(), "")], 0);
        Assert.Empty(unreported.Models);
        Assert.Equal("none", unreported.ContextReduction);
        Assert.Null(unreported.UnverifiedEvidenceCount);
    }

    // ---- refusal, authorization -----------------------------------------------------------------

    [Fact]
    public async Task Source_that_cannot_be_processed_is_still_refused_unchanged_with_nothing_saved_and_no_completeness()
    {
        var (client, _, groqHttp) = GroqRequestBudgetTests.BuildStack(_ => GeminiQuota(), r => GroqOk(PartGeneration(PartOf(r))), maxChunkedParts: 1);
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba, ChunkedAnalysisTests.StructuredSource(12_000));

        var (status, body) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", body!["code"]!.GetValue<string>());
        Assert.False(body["retryable"]!.GetValue<bool>());
        Assert.False(body["sentToProvider"]!.GetValue<bool>());
        Assert.Null(body["completeness"]);
        Assert.Empty(groqHttp.Bodies);
        Assert.Empty(await ArtifactsAsync(ba, projectId, Fr));
    }

    [Fact]
    public async Task Developers_still_cannot_run_analyze_or_generate()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var developer = await host.RegisterAsync(Roles.Developer);
        var (projectId, sourceId) = await SourceAsync(ba);
        Assert.InRange((await host.AddMemberAsync(ba, projectId, developer)).Status, 200, 201);

        Assert.Equal(403, (await developer.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);
        Assert.Equal(403, (await developer.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        Assert.Empty(host.Ai.UserPrompts);
    }
}
