using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AiReap.Tests.GroundedGenerationTests;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// P0-4 — requirement-ID coverage on generation results (whole, split and batched runs) and on
// GET /api/requirement-sources/{id}/source-id-coverage, which is computed from the source text and
// the requirements saved now: re-runs, human edits and rejections are reflected; Developers are
// refused (S2). Scripted AI responses (the test host's stub, or real Gemini/Groq clients over fake
// HTTP) - never a live API; throwaway test databases.
public class SourceIdCoverageIntegrationTests
{
    private const int Fr = 0, Nfr = 1;
    private const string GenerationMarker = "requirementOrigin";

    private const string Srs =
        "## 1. Accounts\n\n" +
        "FR-001: Users must sign in with an email address.\n" +
        "FR-002: Users must be able to reset a forgotten password.\n" +
        "FR-003: Users must be able to sign out on every device.\n\n" +
        "Passwords are hashed with SHA-256.\n\n" +
        "## 2. Performance\n\n" +
        "NFR-001: The sign-in page must load within 2 seconds.";

    private static JsonObject Cites(JsonObject item, string[] sourceIds, string[]? dependsOn = null)
    {
        item["sourceIds"] = new JsonArray(sourceIds.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        if (dependsOn is not null) item["dependsOnSourceIds"] = new JsonArray(dependsOn.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        return item;
    }

    // FR-001, FR-002 (depending on FR-003, which no requirement cites) and NFR-001.
    private static string Generation() => Response(
        [
            Cites(FrItem("Sign in", "explicit", null), ["FR-001"]),
            Cites(FrItem("Reset password", "explicit", null), ["FR-002"], dependsOn: ["FR-003"])
        ],
        [Cites(NfrItem("Sign-in speed", "2 seconds", "stated", null), ["NFR-001"])]);

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static Task<(int Status, JsonNode? Body)> CoverageAsync(ApiUser user, string sourceId) =>
        user.GetAsync($"/api/requirement-sources/{sourceId}/source-id-coverage");

    private static void AssertFirstRunCoverage(JsonNode? coverage)
    {
        Assert.NotNull(coverage);
        Assert.Equal("incomplete", coverage["status"]!.GetValue<string>());
        Assert.Equal(4, coverage["definedCount"]!.GetValue<int>());
        Assert.Equal(["FR-001", "FR-002", "NFR-001"], Strings(coverage["covered"]));
        Assert.Equal(["FR-003"], Strings(coverage["uncovered"])); // dependsOnSourceIds does not cover it
        Assert.Empty(Strings(coverage["coveredOnlyByRejected"]));
        Assert.Equal(["SHA-256"], Strings(coverage["mentionedOnly"]));
    }

    // ---- whole-source run, re-run, compatibility ----------------------------------------------

    [Fact]
    public async Task A_whole_source_run_returns_coverage_matching_the_endpoint_and_keeps_every_existing_field()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (_, sourceId) = await SourceAsync(ba, Srs);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Generation());

        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        AssertFirstRunCoverage(result!["sourceIdCoverage"]);
        // Existing fields unchanged: the artifact lists, their data and the duplicate count.
        Assert.Equal(["functionalRequirements", "nonFunctionalRequirements", "skippedDuplicates", "sourceIdCoverage", "completeness"],
            result.AsObject().Select(kv => kv.Key));
        Assert.Equal(2, result["functionalRequirements"]!.AsArray().Count);
        Assert.Single(result["nonFunctionalRequirements"]!.AsArray());
        Assert.Equal(0, result["skippedDuplicates"]!.GetValue<int>());
        var signIn = result["functionalRequirements"]!.AsArray().Single(a => a!["title"]!.GetValue<string>() == "Sign in")!;
        Assert.Equal(["FR-001"], Strings(signIn["data"]!["sourceIds"]));

        var (cs, coverage) = await CoverageAsync(ba, sourceId);
        Assert.Equal(200, cs);
        AssertFirstRunCoverage(coverage);
        Assert.Equal(result["sourceIdCoverage"]!.ToJsonString(), coverage!.ToJsonString());
    }

    [Fact]
    public async Task A_rerun_that_only_finds_duplicates_still_reports_the_coverage_of_the_saved_requirements()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba, Srs);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Generation());
        await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Generation());
        var (status, rerun) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.Empty(rerun!["functionalRequirements"]!.AsArray());
        Assert.Empty(rerun["nonFunctionalRequirements"]!.AsArray());
        Assert.Equal(3, rerun["skippedDuplicates"]!.GetValue<int>());
        AssertFirstRunCoverage(rerun["sourceIdCoverage"]);
        Assert.Equal(2, (await ArtifactsAsync(ba, projectId, Fr)).Count);
    }

    [Fact]
    public async Task A_source_without_requirement_ids_is_not_applicable()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (_, sourceId) = await SourceAsync(ba, "Employees request leave online; managers approve it. Passwords use SHA-256.");

        var (_, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements"); // stub
        var coverage = result!["sourceIdCoverage"]!;

        Assert.Equal("not_applicable", coverage["status"]!.GetValue<string>());
        Assert.Equal(0, coverage["definedCount"]!.GetValue<int>());
        Assert.Equal(["SHA-256"], Strings(coverage["mentionedOnly"]));
    }

    // ---- the endpoint: edits, rejections, access ----------------------------------------------

    [Fact]
    public async Task The_endpoint_reflects_a_human_edit_that_adds_a_source_id_and_never_counts_rejected_requirements()
    {
        await using var host = new TestHost();
        var ba = await host.ActorAsync(Roles.BusinessAnalyst);
        var reviewer = await host.ActorAsync(Roles.Reviewer);
        var (projectId, sourceId) = await SourceAsync(ba.Api, Srs);
        Assert.InRange((await host.AddMemberAsync(ba.Api, projectId, reviewer.Api)).Status, 200, 201);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Generation());
        var (_, result) = await ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        // A BA adds FR-003 to "Sign in" (as the edit dialog does: the whole data sent back).
        var signIn = result!["functionalRequirements"]!.AsArray().Single(a => a!["title"]!.GetValue<string>() == "Sign in")!;
        var data = (await ba.Api.GetAsync($"/api/artifacts/{Id(signIn)}")).Body!["data"]!.DeepClone().AsObject();
        data["sourceIds"] = new JsonArray("FR-001", "FR-003");
        Assert.Equal(200, (await ba.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{Id(signIn)}", new { data, reason = "covers sign-out too" })).Status);

        var (_, afterEdit) = await CoverageAsync(reviewer.Api, sourceId);
        Assert.Equal("complete", afterEdit!["status"]!.GetValue<string>());
        Assert.Empty(Strings(afterEdit["uncovered"]));

        // The reviewer rejects the NFR: NFR-001 is no longer covered, and is listed as rejected-only.
        var nfr = (await ArtifactsAsync(ba.Api, projectId, Nfr)).Single()!;
        Assert.Equal(200, (await reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{Id(nfr)}/status",
            new { status = 4, comment = "Target not agreed" })).Status);

        var (_, afterReject) = await CoverageAsync(reviewer.Api, sourceId);
        Assert.Equal("incomplete", afterReject!["status"]!.GetValue<string>());
        Assert.Equal(["NFR-001"], Strings(afterReject["uncovered"]));
        Assert.Equal(["NFR-001"], Strings(afterReject["coveredOnlyByRejected"]));
        Assert.DoesNotContain("NFR-001", Strings(afterReject["covered"]));
    }

    [Fact]
    public async Task Developers_are_refused_the_endpoint_and_reviewers_and_business_analysts_may_read_it()
    {
        await using var host = new TestHost();
        var ba = await host.ActorAsync(Roles.BusinessAnalyst);
        var reviewer = await host.ActorAsync(Roles.Reviewer);
        var dev = await host.ActorAsync(Roles.Developer);
        var outsider = await host.ActorAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba.Api, Srs);
        foreach (var member in new[] { reviewer, dev })
        {
            Assert.InRange((await host.AddMemberAsync(ba.Api, projectId, member.Api)).Status, 200, 201);
        }

        var (devStatus, devBody) = await CoverageAsync(dev.Api, sourceId);
        Assert.Equal(403, devStatus);
        Assert.DoesNotContain("FR-001", devBody?.ToJsonString() ?? string.Empty);

        Assert.Equal(200, (await CoverageAsync(reviewer.Api, sourceId)).Status);
        Assert.Equal(200, (await CoverageAsync(ba.Api, sourceId)).Status);
        Assert.Equal(403, (await CoverageAsync(outsider.Api, sourceId)).Status); // not a project member
        Assert.Equal(404, (await CoverageAsync(ba.Api, Guid.NewGuid().ToString())).Status);
    }

    // ---- split and batched runs (real Gemini/Groq clients over fake HTTP) ----------------------

    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";
    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");
    private static readonly Regex ReqId = new(@"REQ-\d+\.\d+");

    private sealed class Http(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Enqueue(body);
            return respond(body);
        }
    }

    // Gemini cooling down, so every part goes to Groq's bounded budget (as in AiChunkRunTests).
    private static (IAiChatClient Client, Http Groq) GroqStack(Func<string, HttpResponseMessage> groq)
    {
        var geminiHttp = new Http(_ => throw new InvalidOperationException("Gemini must not be called."));
        var groqHttp = new Http(groq);
        var resilience = new AiResilienceOptions { BaseDelayMilliseconds = 0 };
        Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;
        var cooldown = new AiProviderCooldown();
        cooldown.CoolDown("Gemini", DateTimeOffset.UtcNow.AddHours(1));
        var client = new FallbackAiChatClient(
            new ResilientAiChatClient(new GeminiAiChatClient(new HttpClient(geminiHttp), Options.Create(new GeminiOptions { ApiKey = GeminiKey })),
                "Gemini", resilience.WithoutRateLimitRetry(), NullLogger.Instance, NoDelay), "Gemini",
            new RequestBudgetAiChatClient(
                new ResilientAiChatClient(new GroqAiChatClient(new HttpClient(groqHttp), Options.Create(new GroqOptions { ApiKey = GroqKey })),
                    "Groq", resilience, NullLogger.Instance, NoDelay), "Groq",
                "Ai:Groq:MaxRequestTokens", 8000, 4000, NullLogger.Instance, 8, "Ai:Groq:MaxChunkedParts"), "Groq",
            cooldown, TimeSpan.FromHours(1), NullLogger.Instance);
        return (client, groqHttp);
    }

    private static (string System, string User) Messages(string groqBody)
    {
        var messages = JsonNode.Parse(groqBody)!["messages"]!.AsArray();
        return (messages[0]!["content"]!.GetValue<string>(), messages[1]!["content"]!.GetValue<string>());
    }

    // Generation: one FR per part citing every requirement ID in that part except REQ-n.6.
    // Analysis: one question per part.
    private static HttpResponseMessage PartResponse(string groqBody)
    {
        var (system, user) = Messages(groqBody);
        var part = PartPattern.Match(user) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
        string content;
        if (system.Contains("missingInformation"))
        {
            content = new JsonObject
            {
                ["actors"] = new JsonArray("Employee"), ["capabilities"] = new JsonArray(), ["dataElements"] = new JsonArray(), ["notes"] = new JsonArray(),
                ["missingInformation"] = new JsonArray(new JsonObject { ["topic"] = $"Part {part}", ["question"] = $"Who approves part {part}?", ["reason"] = "Not stated" })
            }.ToJsonString();
        }
        else
        {
            var ids = ReqId.Matches(user).Select(x => x.Value).Distinct().Where(id => !id.EndsWith(".6")).ToArray();
            content = Response([Cites(FrItem($"Record leave for part {part}", "explicit", null), ids)]);
        }

        return GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content));
    }

    private static string[] ExpectedUncovered(string raw) =>
        SourceIdCoverage.Classify(raw).Defined.Where(id => id.EndsWith(".6")).ToArray();

    [Fact]
    public async Task A_split_run_reports_coverage_over_the_whole_source()
    {
        var (client, groq) = GroqStack(PartResponse);
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var raw = ChunkedAnalysisTests.StructuredSource(9_000);
        var (_, sourceId) = await SourceAsync(ba, raw);

        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.InRange(groq.Bodies.Count, 2, 8);
        var coverage = result!["sourceIdCoverage"]!;
        var expected = ExpectedUncovered(raw);
        Assert.NotEmpty(expected);
        Assert.Equal("incomplete", coverage["status"]!.GetValue<string>());
        Assert.Equal(SourceIdCoverage.Classify(raw).Defined.Count, coverage["definedCount"]!.GetValue<int>());
        Assert.Equal(expected, Strings(coverage["uncovered"]));
        Assert.Equal((await CoverageAsync(ba, sourceId)).Body!.ToJsonString(), coverage.ToJsonString());
    }

    [Fact]
    public async Task A_batched_generation_commit_returns_coverage_and_an_analysis_commit_returns_none()
    {
        var (client, _) = GroqStack(PartResponse);
        await using var host = new TestHost(chatClient: client, settings: new Dictionary<string, string> { ["Ai:Groq:MaxBatchedRunParts"] = "80" });
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var raw = ChunkedAnalysisTests.StructuredSource(30_000);
        var (_, sourceId) = await SourceAsync(ba, raw);

        async Task<JsonNode> CommittedAsync(string operation)
        {
            var (ss, run) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/ai-runs", new { operation });
            Assert.Equal(201, ss);
            while ((await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Body!["status"]!.GetValue<string>() == "Running") { }
            var (cs, committed) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
            Assert.Equal(200, cs);
            Assert.Equal("Committed", committed!["status"]!.GetValue<string>());
            return committed;
        }

        var analysis = await CommittedAsync("analyze");
        Assert.NotEmpty(analysis["result"]!["createdArtifactIds"]!.AsArray());
        Assert.Null(analysis["result"]!["sourceIdCoverage"]);

        var generation = await CommittedAsync("generate");
        var result = generation["result"]!;
        Assert.NotEmpty(result["createdArtifactIds"]!.AsArray());
        Assert.Equal(0, result["skippedDuplicates"]!.GetValue<int>());
        var coverage = result["sourceIdCoverage"]!;
        Assert.Equal("incomplete", coverage["status"]!.GetValue<string>());
        Assert.Equal(ExpectedUncovered(raw), Strings(coverage["uncovered"]));
        Assert.Equal((await CoverageAsync(ba, sourceId)).Body!.ToJsonString(), coverage.ToJsonString());

        // Not stored with the run: a later read of it has no coverage (the endpoint is the current one).
        var reread = (await ba.GetAsync($"/api/ai-runs/{Id(generation)}")).Body!;
        Assert.Null(reread["result"]!["sourceIdCoverage"]);
        Assert.Equal(result["createdArtifactIds"]!.ToJsonString(), reread["result"]!["createdArtifactIds"]!.ToJsonString());
    }
}
