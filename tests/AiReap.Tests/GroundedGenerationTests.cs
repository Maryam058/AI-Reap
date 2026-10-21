using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Artifacts;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// P0-2 — requirement generation records each requirement's origin (as the model gave it) and its
// evidence quotes, checked by the server against the full stored source, the cited clarification
// answers and excerpts. Evidence problems never fail generation and never cause a repair call.
// Scripted AI responses (the test host's stub, or real Gemini/Groq clients over fake HTTP) - never a
// live API; throwaway test databases.
public class GroundedGenerationTests
{
    private const int Fr = 0, Nfr = 1, Cq = 5;

    // In the v4 generation system prompt only.
    private const string GenerationMarker = "requirementOrigin";

    internal const string Srs =
        "## 1. Leave\n\n" +
        "FR-001: Employees must be able to submit a leave request online.\n" +
        "FR-002: Managers must approve leave requests within 2 days.\n\n" +
        "## 2. Performance\n\n" +
        "NFR-001: The leave page must load within 3 seconds for 95% of requests.";

    internal const string Answer = "Approval can be delegated to a deputy manager while the manager is away.";

    // ---- helpers ---------------------------------------------------------------------------

    internal static JsonObject Quote(string from, string quote, string? sourceId = null, string? reference = null) =>
        new() { ["from"] = from, ["quote"] = quote, ["sourceId"] = sourceId, ["ref"] = reference };

    internal static JsonObject FrItem(string title, string? origin, JsonNode? evidence, string expectedResult = "Done", JsonArray? assumed = null) => new()
    {
        ["title"] = title, ["actor"] = "Employee", ["priority"] = "High",
        ["preconditions"] = "Signed in", ["inputs"] = "Dates", ["processing"] = "Store", ["expectedResult"] = expectedResult,
        ["dependencies"] = new JsonArray(), ["businessObjectiveCodes"] = new JsonArray(), ["sourceReferences"] = new JsonArray(),
        ["sourceIds"] = new JsonArray(), ["dependsOnSourceIds"] = new JsonArray(),
        ["requirementOrigin"] = origin, ["derivationNote"] = origin == "explicit" ? null : "Because",
        ["assumedClarifications"] = assumed ?? new JsonArray(), ["evidence"] = evidence ?? new JsonArray()
    };

    internal static JsonObject NfrItem(string title, string? target, string? targetOrigin, JsonNode? evidence, string description = "Fast pages") => new()
    {
        ["title"] = title, ["category"] = "Performance", ["description"] = description, ["targetValue"] = target,
        ["targetOrigin"] = targetOrigin, ["sourceIds"] = new JsonArray(), ["requirementOrigin"] = "explicit",
        ["evidence"] = evidence ?? new JsonArray()
    };

    internal static string Response(JsonArray frs, JsonArray? nfrs = null) =>
        new JsonObject { ["functionalRequirements"] = frs, ["nonFunctionalRequirements"] = nfrs ?? new JsonArray() }.ToJsonString();

    internal static async Task<(string ProjectId, string SourceId)> SourceAsync(ApiUser ba, string rawText = Srs)
    {
        var (ps, project) = await ba.PostAsync("/api/projects", new { name = "Leave", description = "d" });
        Assert.Equal(201, ps);
        var (ss, source) = await ba.PostAsync($"/api/projects/{Id(project)}/requirement-sources", new { sourceType = 1, rawText });
        Assert.InRange(ss, 200, 201);
        return (Id(project), Id(source));
    }

    internal static async Task<JsonArray> ArtifactsAsync(ApiUser user, string projectId, int type)
    {
        var all = (await user.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray();
        return new JsonArray(all.Where(a => a!["artifactType"]!.GetValue<int>() == type).Select(a => a!.DeepClone()).ToArray());
    }

    private static JsonNode ByTitle(JsonNode result, string list, string title) =>
        result[list]!.AsArray().Single(a => a!["title"]!.GetValue<string>() == title)!;

    private static string? Str(JsonNode? node, string key) => node?[key]?.GetValue<string>();

    private static string[] Statuses(JsonNode artifact) =>
        artifact["data"]!["evidence"]!.AsArray().Select(q => q!["status"]!.GetValue<string>()).ToArray();

    private static int GenerationCalls(TestHost host, string sourceText) =>
        host.Ai.UserPrompts.Count(p => p.Contains(sourceText[..40], StringComparison.Ordinal));

    // Analysis (stub: two questions, CQ-001 and CQ-002), then CQ-001 answered.
    internal static async Task AnalyzeAndAnswerAsync(ApiUser ba, string projectId, string sourceId)
    {
        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);
        var cq1 = (await ArtifactsAsync(ba, projectId, Cq)).Single(a => a!["code"]!.GetValue<string>() == "CQ-001")!;
        Assert.Equal(200, (await ba.PostAsync($"/api/artifacts/{Id(cq1)}/clarification-answer", new { answer = Answer, notApplicable = false })).Status);
    }

    // ---- whole-source run --------------------------------------------------------------------

    [Fact]
    public async Task A_whole_source_run_stores_origin_and_checked_evidence_and_every_target_stays_a_proposed_assumption()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);
        await AnalyzeAndAnswerAsync(ba, projectId, sourceId);

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response(
            [
                FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", "Employees must be able to submit a leave request online", "FR-001"))),
                FrItem("Delegate approval", "derived", new JsonArray(
                    Quote("clarification", "Approval can be delegated to a deputy manager", reference: "CQ-001"))),
                FrItem("Multi-level approval", "assumed", null, assumed: new JsonArray("CQ-002")),
                FrItem("Approve quickly", "explicit",
                    new JsonArray(Quote("srs", "Managers must approve leave requests within 2 days", "FR-002")),
                    expectedResult: "Approved within 2 days; escalated after 8 hours")
            ],
            [
                NfrItem("Page load", "3 seconds", "stated", new JsonArray(Quote("srs", "The leave page must load within 3 seconds for 95% of requests", "NFR-001"))),
                NfrItem("Availability", "99.9%", "proposed", null, description: "The service is available"),
                NfrItem("Search speed", "1 second", "stated", new JsonArray(Quote("clarification", "Approval can be delegated to a deputy manager", reference: "CQ-001")))
            ]));

        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, status);

        // The generation prompt lists clarifications by code; the unanswered one says to cite it.
        var prompt = host.Ai.UserPrompts.Last(p => p.Contains("FR-001: Employees", StringComparison.Ordinal));
        Assert.Contains($"- CQ-001 Q: ", prompt);
        Assert.Contains($"  A: {Answer}\n", prompt);
        Assert.Contains("cite CQ-002 in assumedClarifications", prompt);

        var submit = ByTitle(result!, "functionalRequirements", "Submit leave")["data"]!;
        Assert.Equal("explicit", Str(submit, "requirementOrigin"));
        Assert.Equal("verified", Str(submit, "evidenceStatus"));
        Assert.Equal("SourceEvidence-v1", Str(submit, "evidenceCheck"));
        Assert.Equal("strict", Str(submit["evidence"]![0], "matchMode"));

        var delegate_ = ByTitle(result!, "functionalRequirements", "Delegate approval")["data"]!;
        Assert.Equal(("derived", "verified", "clarification", "CQ-001"),
            (Str(delegate_, "requirementOrigin"), Str(delegate_, "evidenceStatus"), Str(delegate_["evidence"]![0], "from"), Str(delegate_["evidence"]![0], "ref")));

        var assumed = ByTitle(result!, "functionalRequirements", "Multi-level approval")["data"]!;
        Assert.Equal(("assumed", "none"), (Str(assumed, "requirementOrigin"), Str(assumed, "evidenceStatus")));
        Assert.Equal(["CQ-002"], assumed["assumedClarifications"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("Because", Str(assumed, "derivationNote"));

        var quick = ByTitle(result!, "functionalRequirements", "Approve quickly")["data"]!;
        Assert.Equal(["8 hours"], quick["unsupportedNumbers"]!.AsArray().Select(n => n!.GetValue<string>()));

        var pageLoad = ByTitle(result!, "nonFunctionalRequirements", "Page load")["data"]!;
        Assert.Equal(("stated", "quote_found", "proposed_assumption"),
            (Str(pageLoad, "targetOrigin"), Str(pageLoad, "targetSupport"), Str(pageLoad, "assumptionStatus")));
        Assert.Empty(pageLoad["unsupportedNumbers"]!.AsArray());

        var availability = ByTitle(result!, "nonFunctionalRequirements", "Availability")["data"]!;
        Assert.Equal(("proposed", null, "proposed_assumption"),
            (Str(availability, "targetOrigin"), Str(availability, "targetSupport"), Str(availability, "assumptionStatus")));
        Assert.Equal(["99.9%"], availability["unsupportedNumbers"]!.AsArray().Select(n => n!.GetValue<string>()));

        // A clarification quote is never SRS support for a "stated" target.
        var search = ByTitle(result!, "nonFunctionalRequirements", "Search speed")["data"]!;
        Assert.Equal(("verified", "not_supported", "proposed_assumption"),
            (Str(search, "evidenceStatus"), Str(search, "targetSupport"), Str(search, "assumptionStatus")));

        Assert.Equal(7, await host.ScalarAsync(
            "SELECT COUNT(*) FROM AIExecutions WHERE OperationType = 'RequirementGeneration' AND PromptTemplateVersion = 'RequirementGeneration-v4'"));
    }

    [Fact]
    public async Task Requirement_origin_is_stored_exactly_as_given_for_every_origin_and_evidence_status()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);

        var evidence = new Dictionary<string, Func<JsonArray>>
        {
            ["verified"] = () => new JsonArray(Quote("srs", "Employees must be able to submit a leave request online")),
            ["unverified"] = () => new JsonArray(Quote("srs", "Employees may cancel approved leave at any time")),
            ["none"] = () => new JsonArray()
        };
        string?[] origins = ["explicit", "derived", "assumed", "Explicit ", "stated-in-meeting", null];
        // Titles by index: "explicit" and "Explicit " would be one requirement (ArtifactFingerprints).
        var frs = new JsonArray();
        for (var o = 0; o < origins.Length; o++)
        foreach (var (status, quotes) in evidence)
        {
            frs.Add(FrItem($"FR {o} {status}", origins[o], quotes()));
        }

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response(frs));
        var (code, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, code);

        for (var o = 0; o < origins.Length; o++)
        foreach (var status in evidence.Keys)
        {
            var data = ByTitle(result!, "functionalRequirements", $"FR {o} {status}")["data"]!;
            Assert.Equal(origins[o], Str(data, "requirementOrigin"));
            Assert.Equal(status, Str(data, "evidenceStatus"));
        }

        Assert.Equal(1, GenerationCalls(host, Srs)); // no repair for any origin value
        Assert.Equal(origins.Length * evidence.Count, (await ArtifactsAsync(ba, projectId, Fr)).Count);
    }

    // ---- retries ---------------------------------------------------------------------------

    [Fact]
    public async Task Malformed_or_fabricated_evidence_never_causes_a_repair_call()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (_, sourceId) = await SourceAsync(ba);

        var odd = FrItem("Odd shapes", null, JsonValue.Create("Employees must be able to submit a leave request online"));
        odd["requirementOrigin"] = 5;
        odd["derivationNote"] = new JsonObject { ["why"] = "x" };
        odd["assumedClarifications"] = "CQ-009";
        var fabricated = FrItem("Fabricated", "explicit", new JsonArray(
            Quote("srs", "The system must store every leave request in the blockchain"),
            new JsonObject { ["from"] = 7, ["quote"] = 12, ["status"] = "verified" },
            "not even an object"));
        var nfr = NfrItem("Odd target", "2 seconds", null, new JsonObject { ["from"] = "srs", ["quote"] = "Managers must approve leave requests within 2 days" });
        nfr["targetOrigin"] = new JsonArray("stated");

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response([odd, fabricated], [nfr]));
        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.Equal(1, GenerationCalls(host, Srs));

        var oddData = ByTitle(result!, "functionalRequirements", "Odd shapes")["data"]!;
        Assert.Equal("5", Str(oddData, "requirementOrigin")); // kept as given (its JSON text), not reclassified
        Assert.Equal(["CQ-009"], oddData["assumedClarifications"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("invalid_source_type", Str(oddData["evidence"]![0], "reason")); // a bare string has no "from"

        var fabricatedData = ByTitle(result!, "functionalRequirements", "Fabricated")["data"]!;
        Assert.Equal("unverified", Str(fabricatedData, "evidenceStatus"));
        Assert.Equal(2, fabricatedData["evidence"]!.AsArray().Count); // at most two kept
        Assert.Equal(["not_found", "invalid_source_type"], fabricatedData["evidence"]!.AsArray().Select(q => Str(q, "reason")));

        var nfrData = ByTitle(result!, "nonFunctionalRequirements", "Odd target")["data"]!;
        Assert.Equal("verified", Str(nfrData, "evidenceStatus"));
        Assert.Null(Str(nfrData, "targetSupport")); // ["stated"] is not the claim "stated"
    }

    [Fact]
    public async Task A_real_validation_error_still_gets_exactly_one_repair()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);

        var invalid = FrItem("No actor", "explicit", new JsonArray(Quote("srs", "invented words that are nowhere")));
        invalid["actor"] = "";
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response([invalid]));
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response([FrItem("Submit leave", "explicit", null)]));

        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);

        Assert.Equal(2, GenerationCalls(host, Srs));
        var original = host.Ai.UserPrompts[^2];
        var repair = host.Ai.UserPrompts[^1];
        Assert.StartsWith(original, repair);
        var instruction = repair[original.Length..];
        Assert.Contains("actor", instruction);
        Assert.DoesNotContain("evidence", instruction, StringComparison.OrdinalIgnoreCase); // the invented quote is not an error
        Assert.Single(await ArtifactsAsync(ba, projectId, Fr));
    }

    [Fact]
    public async Task Rerunning_identical_content_adds_nothing_and_leaves_the_stored_evidence_untouched()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);
        var first = Response([FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", "Employees must be able to submit a leave request online")))]);
        // The same requirement content, now with an invented quote and another origin.
        var second = Response([FrItem("Submit leave", "assumed", new JsonArray(Quote("srs", "made up words entirely absent from it")))]);

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, first);
        await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        var before = (await ArtifactsAsync(ba, projectId, Fr)).Single()!["data"]!.ToJsonString();

        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, second);
        var (_, rerun) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Empty(rerun!["functionalRequirements"]!.AsArray());
        Assert.Equal(1, rerun["skippedDuplicates"]!.GetValue<int>());
        var after = (await ArtifactsAsync(ba, projectId, Fr)).Single()!;
        Assert.Equal(before, after["data"]!.ToJsonString());
        Assert.Equal(1, after["currentVersion"]!.GetValue<int>());
    }

    // ---- old records, fingerprints, lifecycle -------------------------------------------------

    [Fact]
    public async Task The_fingerprint_is_the_content_fingerprint_whatever_the_evidence()
    {
        await using var host = new TestHost();
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response(
            [FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", "Employees must be able to submit a leave request online")))],
            [NfrItem("Page load", "3 seconds", "stated", new JsonArray(Quote("srs", "The leave page must load within 3 seconds")))]));
        await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        var fr = ArtifactFingerprints.ForFunctional("Employee", "Submit leave", "Signed in", "Dates", "Store", "Done");
        var nfr = ArtifactFingerprints.ForNonFunctional("Performance", "Page load", "Fast pages", "3 seconds");
        Assert.Equal(1, await host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactFingerprints WHERE Fingerprint = '{fr}'"));
        Assert.Equal(1, await host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactFingerprints WHERE Fingerprint = '{nfr}'"));
        Assert.Equal(2, await host.ScalarAsync("SELECT COUNT(*) FROM ArtifactFingerprints"));
        _ = projectId;
    }

    [Fact]
    public async Task A_record_made_before_evidence_checks_still_displays_edits_approves_and_confirms_without_invented_evidence()
    {
        await using var host = new TestHost();
        var ba = await host.ActorAsync(Roles.BusinessAnalyst);
        var reviewer = await host.ActorAsync(Roles.Reviewer);
        var (projectId, sourceId) = await SourceAsync(ba.Api);
        Assert.InRange((await host.AddMemberAsync(ba.Api, projectId, reviewer.Api)).Status, 200, 201);
        Assert.Equal(200, (await ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status); // stub

        // Rewrite both to their pre-P0-2 shape (as generated by v3).
        const string legacyFr = """{"Actor":"Employee","Preconditions":"Employee is authenticated.","Inputs":"Request type, details","Processing":"Validate input and persist the request.","ExpectedResult":"Request stored with status Pending.","Dependencies":[],"SourceReferences":[],"SourceIds":[],"DependsOnSourceIds":[]}""";
        const string legacyNfr = """{"Category":"Performance","Description":"The system should respond to user actions promptly.","TargetValue":"2 seconds","AssumptionStatus":"proposed_assumption","SourceIds":[]}""";
        await host.ExecuteAsync($"UPDATE Artifacts SET DataJson = N'{legacyFr}' WHERE ArtifactType = 0; UPDATE ArtifactVersions SET DataSnapshotJson = N'{legacyFr}' WHERE ArtifactId IN (SELECT Id FROM Artifacts WHERE ArtifactType = 0)");
        await host.ExecuteAsync($"UPDATE Artifacts SET DataJson = N'{legacyNfr}' WHERE ArtifactType = 1; UPDATE ArtifactVersions SET DataSnapshotJson = N'{legacyNfr}' WHERE ArtifactId IN (SELECT Id FROM Artifacts WHERE ArtifactType = 1)");

        var fr = (await ArtifactsAsync(ba.Api, projectId, Fr)).Single()!;
        Assert.Null(fr["data"]!["evidenceCheck"]);
        Assert.Null(fr["data"]!["evidence"]);

        // An edit that tries to add evidence results stores the edit, without them.
        var (es, edited) = await ba.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{Id(fr)}", new
        {
            data = new { actor = "Employee", expectedResult = "Request stored within 5 seconds.", evidenceStatus = "verified", evidence = new[] { new { quote = "x" } } },
            reason = "edit"
        });
        Assert.Equal(200, es);
        Assert.Equal("Request stored within 5 seconds.", Str(edited!["data"], "expectedResult"));
        Assert.Null(edited["data"]!["evidenceStatus"]);
        Assert.Null(edited["data"]!["evidence"]);
        Assert.Null(edited["data"]!["unsupportedNumbers"]);

        Assert.Equal(200, (await reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{Id(fr)}/status", new { status = 3 })).Status);

        var nfr = (await ArtifactsAsync(ba.Api, projectId, Nfr)).Single()!;
        var (cs, confirmed) = await reviewer.Api.PostAsync($"/api/artifacts/{Id(nfr)}/nfr-confirmation", new { version = 1 });
        Assert.Equal(200, cs);
        Assert.Equal("confirmed", Str(confirmed!["data"], "assumptionStatus"));
        Assert.Null(confirmed["data"]!["evidenceCheck"]);
        Assert.Null(confirmed["data"]!["targetSupport"]);
    }

    [Fact]
    public async Task Approval_new_versions_and_nfr_confirmation_keep_the_evidence_in_every_snapshot()
    {
        await using var host = new TestHost();
        var ba = await host.ActorAsync(Roles.BusinessAnalyst);
        var reviewer = await host.ActorAsync(Roles.Reviewer);
        var (projectId, sourceId) = await SourceAsync(ba.Api);
        Assert.InRange((await host.AddMemberAsync(ba.Api, projectId, reviewer.Api)).Status, 200, 201);
        host.Ai.RespondOnceWhenPromptContains(GenerationMarker, Response(
            [FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", "Employees must be able to submit a leave request online")))],
            [NfrItem("Page load", "3 seconds", "stated", new JsonArray(Quote("srs", "The leave page must load within 3 seconds")))]));
        var (_, result) = await ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        var frId = Id(result!["functionalRequirements"]![0]);
        var nfrId = Id(result["nonFunctionalRequirements"]![0]);

        Assert.Equal(200, (await reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = 3 })).Status);
        var current = (await ba.Api.GetAsync($"/api/artifacts/{frId}")).Body!;
        var data = current["data"]!.DeepClone().AsObject(); // the edit dialog sends the data back whole
        data["actor"] = "Staff member";
        Assert.Equal(200, (await ba.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { data, reason = "rename actor" })).Status);
        Assert.Equal(200, (await reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = 3 })).Status);

        var versions = (await ba.Api.GetAsync($"/api/artifacts/{frId}/versions")).Body!.AsArray();
        Assert.Equal(2, versions.Count);
        Assert.All(versions, v => Assert.Equal("verified", Str(v!["data"], "evidenceStatus")));
        Assert.All(versions, v => Assert.Equal("Employees must be able to submit a leave request online", Str(v!["data"]!["evidence"]![0], "quote")));

        var (cs, confirmed) = await reviewer.Api.PostAsync($"/api/artifacts/{nfrId}/nfr-confirmation", new { version = 1 });
        Assert.Equal(200, cs);
        Assert.Equal(("confirmed", "quote_found", "verified"),
            (Str(confirmed!["data"], "assumptionStatus"), Str(confirmed["data"], "targetSupport"), Str(confirmed["data"], "evidenceStatus")));
    }

    // ---- split and batched runs (real Gemini/Groq clients over fake HTTP) ----------------------

    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";
    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");

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

    // Gemini cooling down, so every part goes to Groq's bounded budget (AiChunkRunTests.BuildStack).
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

    private static string UserPrompt(string groqBody) => JsonNode.Parse(groqBody)!["messages"]![1]!["content"]!.GetValue<string>();

    private static int PartOf(string groqBody) => PartPattern.Match(UserPrompt(groqBody)) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    // Every part restates "Request leave" (merged away) with a quote from its own part, and adds an FR
    // quoting section 1 - text only part 1 was sent.
    private static HttpResponseMessage PartGeneration(string groqBody)
    {
        var part = PartOf(groqBody);
        var own = Regex.Match(UserPrompt(groqBody), @"Managers see their team calendar for area \d+").Value;
        var content = Response(
        [
            FrItem("Request leave", "explicit", new JsonArray(Quote("srs", own))),
            FrItem($"Record leave for part {part}", "derived", new JsonArray(Quote("srs", "Employees see their balance for area 1")))
        ]);
        return GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content));
    }

    [Fact]
    public async Task A_split_run_checks_every_quote_against_the_full_source_and_merges_duplicate_evidence()
    {
        var (client, groq) = GroqStack(PartGeneration);
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var raw = ChunkedAnalysisTests.StructuredSource(9_000);
        var (projectId, sourceId) = await SourceAsync(ba, raw);

        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, status);
        var parts = groq.Bodies.Count;
        Assert.InRange(parts, 2, 8);
        Assert.All(groq.Bodies.Where(b => PartOf(b) > 1), b => Assert.DoesNotContain("balance for area 1.", UserPrompt(b)));

        var frs = result!["functionalRequirements"]!.AsArray();
        Assert.Equal(parts + 1, frs.Count);
        foreach (var fr in frs.Where(f => f!["title"]!.GetValue<string>().StartsWith("Record leave")))
        {
            Assert.Equal("verified", Str(fr!["data"], "evidenceStatus"));
        }

        // The merged duplicate keeps two quotes, from the first two parts, each verified.
        var merged = frs.Single(f => f!["title"]!.GetValue<string>() == "Request leave")!;
        Assert.Equal(["verified", "verified"], Statuses(merged));
        var quotes = merged["data"]!["evidence"]!.AsArray().Select(q => Str(q, "quote")).ToList();
        Assert.Equal(2, quotes.Distinct().Count());
        _ = projectId;
    }

    [Fact]
    public async Task A_batched_run_keeps_evidence_in_its_parts_checks_it_at_commit_and_a_stale_run_is_refused()
    {
        var (client, _) = GroqStack(PartGeneration);
        await using var host = new TestHost(chatClient: client, settings: new Dictionary<string, string> { ["Ai:Groq:MaxBatchedRunParts"] = "80" });
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba, ChunkedAnalysisTests.StructuredSource(30_000));

        async Task<JsonNode> StagedRunAsync(string source)
        {
            var (ss, run) = await ba.PostAsync($"/api/requirement-sources/{source}/ai-runs", new { operation = "generate" });
            Assert.Equal(201, ss);
            while ((await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Body!["status"]!.GetValue<string>() == "Running") { }
            return run!;
        }

        var run = await StagedRunAsync(sourceId);
        Assert.Equal(1, await host.ScalarAsync($"SELECT COUNT(*) FROM AiChunkRuns WHERE Id = '{Id(run)}' AND PromptTemplateVersion = 'RequirementGeneration-v4'"));
        Assert.True(await host.ScalarAsync($"SELECT COUNT(*) FROM AiChunkRunParts WHERE AiChunkRunId = '{Id(run)}' AND ParsedJson LIKE '%balance for area 1%'") > 1);

        var (cs, committed) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
        Assert.Equal(200, cs);
        Assert.Equal("Committed", committed!["status"]!.GetValue<string>());
        var frs = await ArtifactsAsync(ba, projectId, Fr);
        Assert.All(frs, fr => Assert.Equal("verified", Str(fr!["data"], "evidenceStatus")));
        Assert.All(frs, fr => Assert.Equal("SourceEvidence-v1", Str(fr!["data"], "evidenceCheck")));

        // A run whose frozen prompt context no longer matches (as a run started under v3 would not) is
        // refused at commit and saves nothing. Simulated by changing its recorded context hash.
        var (_, project2) = await ba.PostAsync("/api/projects", new { name = "Leave 2", description = "d" });
        var (_, source2) = await ba.PostAsync($"/api/projects/{Id(project2)}/requirement-sources",
            new { sourceType = 1, rawText = ChunkedAnalysisTests.StructuredSource(30_000) });
        var stale = await StagedRunAsync(Id(source2));
        await host.ExecuteAsync($"UPDATE AiChunkRuns SET ContextHash = 'v3' WHERE Id = '{Id(stale)}'");
        Assert.Equal(409, (await ba.PostAsync($"/api/ai-runs/{Id(stale)}/commit")).Status);
        Assert.Equal("Superseded", (await ba.GetAsync($"/api/ai-runs/{Id(stale)}")).Body!["status"]!.GetValue<string>());
        Assert.Empty(await ArtifactsAsync(ba, Id(project2), Fr));
    }
}
