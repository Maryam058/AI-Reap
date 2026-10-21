using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;
using static AiReap.Tests.GroundedGenerationTests;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// P0-5 Phase 1 — MEASUREMENT ONLY. Changes no production behaviour and asserts no policy: every stage
// runs through its real service (TestHost, scripted/stub AI - never a live provider), the exact
// (system, user) prompt each one sends is captured, and the production estimator and reducer are
// applied to it offline, against Groq's configured input budget:
//   - estimated request tokens (PromptBudgetReducer.EstimateRequestTokens);
//   - passages presented to / kept / omitted by PromptBudgetReducer.Fit, i.e. what the condensing
//     CompleteAsync path (RequestBudgetAiChatClient) would send;
//   - whether the lossless path would send it whole (ChunkPlanner.SendsWhole, the check
//     CompleteUncondensedAsync applies) or refuse it - or, for Analyze/Generate, which already split
//     instead of condensing, the ChunkPlanner outcome;
//   - for prompts built from requirement blocks (quality, conflicts, tests), which blocks are lost
//     whole or in part; for prompts carrying the source, which requirement IDs disappear.
// Synthetic sources (ChunkedAnalysisTests.StructuredSource) and synthetic artifacts only; no real
// database or source is read. Results: test output and p05-measurements-<case>.md next to the test
// assembly (git-ignored build folder).
//
// Deferred (not measured): Project Copilot and ReviewAgent.
public class ContextReductionMeasurementTests
{
    // appsettings.json Ai:Groq: MaxRequestTokens 8000, ReservedOutputTokens 4000, MaxChunkedParts 8.
    private const int GroqInputBudget = 8000 - 4000;
    private const int GroqMaxParts = 8;

    private readonly ITestOutputHelper _output;

    public ContextReductionMeasurementTests(ITestOutputHelper output) => _output = output;

    // Records every call and answers requirement generation with the scripted response; every other
    // stage gets the development stub's canned answer.
    private sealed class RecordingAiChatClient(Func<string> generation) : IAiChatClient
    {
        private readonly StubAiChatClient _stub = new();
        public List<(string System, string User)> Calls { get; } = new();
        public string ModelName => _stub.ModelName;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add((systemPrompt, userPrompt));
            return systemPrompt.Contains("requirementOrigin")
                ? Task.FromResult(generation())
                : _stub.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }
    }

    // The stage of a call, by the same system-prompt keywords StubAiChatClient routes on.
    private static readonly (string Keyword, string Stage)[] Stages =
    [
        ("readinessSummary", "Review (agent)"), ("missingInformation", "Analyze"), ("functionalRequirements", "Generate FR/NFR"),
        ("userStories", "User stories"), ("acceptanceCriteria", "Acceptance criteria"), ("businessRules", "Business rules"),
        ("relationshipType", "Conflict detection"), ("artifactCode", "Quality review"), ("architectureOverview", "Solution design"),
        ("dataEntities", "Data entities"), ("apiSpecifications", "API specifications"), ("taskType", "Implementation tasks"),
        ("testCases", "Test cases"), ("citedChunkRefs", "Copilot")
    ];

    private static string StageOf(string systemPrompt) =>
        Stages.FirstOrDefault(s => systemPrompt.Contains(s.Keyword)).Stage ?? "unknown";

    // One FR per requirement ID (the first frCount), each citing it with a verified quote - the P0-2
    // payload a real v4 generation stores - plus three NFRs.
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

    public sealed record Measurement(
        string Case, string Stage, int EstimatedTokens, int Budget, int Passages, int Kept, int Omitted,
        bool Condensed, bool CondensedFits, string Lossless, string Loss);

    [Theory]
    [InlineData("small", 2_500, 10)]
    [InlineData("medium", 8_000, 25)]
    [InlineData("large", 30_000, 60)]
    public async Task Measure_every_requirement_engineering_stage(string name, int sourceTokens, int frCount)
    {
        var source = ChunkedAnalysisTests.StructuredSource(sourceTokens);
        var ai = new RecordingAiChatClient(() => SyntheticGeneration(source, frCount));
        await using var host = new TestHost(chatClient: ai);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba, source);

        async Task<JsonNode?> Run(string path)
        {
            var (status, body) = await ba.PostAsync(path);
            Assert.True(status == 200, $"{path} returned {status}: {body?.ToJsonString()}");
            return body;
        }

        await Run($"/api/requirement-sources/{sourceId}/analyze");
        var generated = await Run($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(frCount, generated!["functionalRequirements"]!.AsArray().Count);
        var stories = await Run($"/api/requirement-sources/{sourceId}/generate-user-stories");
        await Run($"/api/artifacts/{Id(stories![0])}/generate-acceptance-criteria");
        await Run($"/api/requirement-sources/{sourceId}/generate-business-rules");
        await Run($"/api/requirement-sources/{sourceId}/analyze-quality");
        await Run($"/api/projects/{projectId}/detect-conflicts");
        await Run($"/api/requirement-sources/{sourceId}/generate-design");
        await Run($"/api/requirement-sources/{sourceId}/generate-data-entities");
        await Run($"/api/requirement-sources/{sourceId}/generate-api-specs");
        await Run($"/api/requirement-sources/{sourceId}/generate-tasks");
        await Run($"/api/artifacts/{Id(generated["functionalRequirements"]![0])}/generate-test-cases");

        var results = ai.Calls.Select(c => Measure(name, c.System, c.User, source)).ToList();

        // Every stage in scope was reached through its real service (Copilot and the review agent are deferred).
        string[] expected =
        [
            "Analyze", "Generate FR/NFR", "User stories", "Acceptance criteria", "Business rules", "Quality review",
            "Conflict detection", "Solution design", "Data entities", "API specifications", "Implementation tasks", "Test cases"
        ];
        Assert.Equal(expected.Order(), results.Select(r => r.Stage).Distinct().Order());

        // Internal consistency of the measurement itself (not a policy).
        Assert.All(results, r =>
        {
            Assert.Equal(r.Passages, r.Kept + r.Omitted);
            Assert.Equal(r.EstimatedTokens <= r.Budget, !r.Condensed);
        });

        Report(name, sourceTokens, frCount, results);
    }

    private static Measurement Measure(string name, string system, string user, string source)
    {
        var stage = StageOf(system);
        var tokens = PromptBudgetReducer.EstimateRequestTokens(system, user);
        var fit = PromptBudgetReducer.Fit(system, user, GroqInputBudget);

        // Fit reports passage counts only when it has to reduce; a 1-token budget makes it split the
        // prompt into the same droppable passages without changing anything, to count them.
        var presented = PromptBudgetReducer.Fit(system, user, 1).TotalPassages;
        if (fit.WasReduced) Assert.Equal(presented, fit.TotalPassages);
        var kept = fit.WasReduced ? fit.KeptPassages : presented;
        string lossless;
        if (stage is "Analyze" or "Generate FR/NFR")
        {
            // These already go lossless-or-split (CallAiAndParseChunkedAsync). The whole prompt is the
            // source followed by its context (ChunkPlanner.WholePrompt); the context is treated as
            // fixed here, so clarification sharing across parts is not modelled.
            Assert.StartsWith(source, user);
            var plan = ChunkPlanner.Plan(system, source, user[source.Length..], GenerationSupport.SharedContext.None, GroqInputBudget, GroqMaxParts);
            lossless = plan.Outcome == ChunkPlanOutcome.Split ? $"split, {plan.Chunks.Count} parts" : plan.Outcome.ToString();
        }
        else
        {
            lossless = ChunkPlanner.SendsWhole(fit) ? "fits" : "refused";
        }

        return new Measurement(name, stage, tokens, GroqInputBudget, presented, kept, presented - kept,
            fit.WasReduced, fit.Fits, lossless, fit.WasReduced ? Loss(user, fit.UserPrompt) : "-");
    }

    private static readonly Regex BlockHeader = new(@"^((?:FR|NFR)-\d{3,}): ", RegexOptions.Multiline);
    private static readonly Regex RequirementId = new(@"REQ-\d+\.\d+\b");

    // What the condensed prompt no longer carries: requirement blocks ("FR-001: title\n{data}") lost
    // whole (header gone) or in part (header kept, block not intact), and source requirement IDs gone.
    private static string Loss(string original, string condensed)
    {
        var parts = new List<string>();
        var blocks = original.Replace("\r\n", "\n").Split("\n\n").Where(b => BlockHeader.IsMatch(b)).ToList();
        if (blocks.Count > 0)
        {
            var lost = blocks.Count(b => !condensed.Contains(BlockHeader.Match(b).Value, StringComparison.Ordinal));
            var intact = blocks.Count(b => condensed.Contains(b.Trim(), StringComparison.Ordinal));
            parts.Add($"requirement blocks: {lost} of {blocks.Count} lost whole, {blocks.Count - lost - intact} in part");
        }

        var ids = RequirementId.Matches(original).Select(m => m.Value).ToHashSet();
        if (ids.Count > 0)
        {
            var kept = RequirementId.Matches(condensed).Select(m => m.Value).ToHashSet();
            parts.Add($"source requirement IDs: {ids.Count(i => !kept.Contains(i))} of {ids.Count} gone");
        }

        return parts.Count == 0 ? "passages only" : string.Join("; ", parts);
    }

    private void Report(string name, int sourceTokens, int frCount, IReadOnlyList<Measurement> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Case `{name}`: synthetic source ~{sourceTokens:N0} request tokens, {frCount} FRs + 3 NFRs (Groq input budget {GroqInputBudget:N0})");
        sb.AppendLine();
        sb.AppendLine("| Stage | Est. tokens | Passages | Kept | Omitted | Condensed? | Condensed fits? | Lossless path | Lost when condensed |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|---|---|---|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r.Stage} | {r.EstimatedTokens:N0} | {r.Passages} | {r.Kept} | {r.Omitted} | {(r.Condensed ? "yes" : "no")} | " +
                          $"{(r.Condensed ? (r.CondensedFits ? "yes" : "no (refused)") : "-")} | {r.Lossless} | {r.Loss} |");
        }

        var text = sb.ToString();
        _output.WriteLine(text);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"p05-measurements-{name}.md"), text);
    }
}
