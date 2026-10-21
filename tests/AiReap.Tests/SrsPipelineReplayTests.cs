using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Phase 0 / T3 — scripted AI responses replayed through the REAL RequirementAnalysisService and
// RequirementGenerationService over InMemoryAiReapDb, then scored from what they persisted. Shows the
// product's own evidence checks and the scorer agree end to end. Offline: the scripted client returns
// fixed text; no AI provider, no SQL Server, no network, and no production code is changed.
[Trait("Category", "Phase0")]
public class SrsPipelineReplayTests(ITestOutputHelper output)
{
    private static string Source => GoldenFixture.Source(GoldenFixture.ClinicBooking);
    private static FixtureExpectation Expected => GoldenFixture.Expected(GoldenFixture.ClinicBooking);

    private static (InMemoryAiReapDb Db, RequirementSource Source) Seed()
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Name = "Phase 0 synthetic project", CreatedByUserId = "phase0-offline-user" };
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = Source,
            CreatedByUserId = "phase0-offline-user"
        };
        db.Projects.Add(project);
        db.RequirementSources.Add(source);
        return (db, source);
    }

    private static RequirementGenerationService Generation(InMemoryAiReapDb db, ScriptedAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new NoDocumentsRetriever(), new BatchModeOff(),
            NullLogger<RequirementGenerationService>.Instance);

    private static RequirementAnalysisService Analysis(InMemoryAiReapDb db, ScriptedAiChatClient ai) =>
        new(ai, db, new OfflineCurrentUser(), new AllowAllProjectAccess(), new BatchModeOff(), NullLogger<RequirementAnalysisService>.Instance);

    // A candidate in the RequirementGeneration-v4 response shape the service parses.
    private static string GenerationResponse(CandidateOutput candidate)
    {
        JsonArray Evidence(CandidateRequirement c) =>
            new(c.Evidence.Select(q => (JsonNode)new JsonObject { ["from"] = q.From, ["quote"] = q.Quote, ["sourceId"] = q.SourceId, ["ref"] = q.Ref }).ToArray());
        JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

        var frs = candidate.Requirements.Where(c => c.Kind == "FR").Select(c => (JsonNode)new JsonObject
        {
            ["title"] = c.Title, ["actor"] = c.Actor, ["priority"] = "High",
            ["preconditions"] = null, ["inputs"] = null, ["processing"] = null, ["expectedResult"] = c.Text,
            ["dependencies"] = new JsonArray(), ["businessObjectiveCodes"] = new JsonArray(), ["sourceReferences"] = new JsonArray(),
            ["sourceIds"] = Strings(c.SourceIds), ["dependsOnSourceIds"] = new JsonArray(),
            ["requirementOrigin"] = c.Origin, ["derivationNote"] = null, ["assumedClarifications"] = new JsonArray(), ["evidence"] = Evidence(c)
        }).ToArray();
        var nfrs = candidate.Requirements.Where(c => c.Kind == "NFR").Select(c => (JsonNode)new JsonObject
        {
            ["title"] = c.Title, ["category"] = c.Category, ["description"] = c.Text,
            ["targetValue"] = c.TargetValue, ["targetOrigin"] = c.TargetOrigin, ["sourceIds"] = Strings(c.SourceIds),
            ["requirementOrigin"] = c.Origin, ["derivationNote"] = null, ["assumedClarifications"] = new JsonArray(), ["evidence"] = Evidence(c)
        }).ToArray();
        return new JsonObject { ["functionalRequirements"] = new JsonArray(frs), ["nonFunctionalRequirements"] = new JsonArray(nfrs) }.ToJsonString();
    }

    private static List<Artifact> Persisted(InMemoryAiReapDb db, ArtifactType type) =>
        InMemoryAiReapDb.Rows(db.Artifacts).Where(a => a.ArtifactType == type).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Scripted_generation_through_the_service_persists_evidence_that_scores_as_the_candidate_does()
    {
        var (db, source) = Seed();
        var candidate = GoldenFixture.Candidate("generation-perfect");
        var ai = new ScriptedAiChatClient(GenerationResponse(candidate));

        var result = await Generation(db, ai).GenerateAsync(source.Id);

        // One AI call (no repair), carrying the whole stored source.
        Assert.Single(ai.Prompts);
        Assert.Contains(Source, ai.Prompts[0].User);
        Assert.Equal(11, result.FunctionalRequirements.Count);
        Assert.Equal(5, result.NonFunctionalRequirements.Count);

        var frs = Persisted(db, ArtifactType.FunctionalRequirement);
        var nfrs = Persisted(db, ArtifactType.NonFunctionalRequirement);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Assert.All(frs, a => Assert.Equal("verified", JsonSerializer.Deserialize<FunctionalRequirementPayload>(a.DataJson, options)!.EvidenceStatus));
        var nfrPayloads = nfrs.Select(a => JsonSerializer.Deserialize<NonFunctionalRequirementPayload>(a.DataJson, options)!).ToList();
        Assert.All(nfrPayloads, p => Assert.Equal("verified", p.EvidenceStatus));
        Assert.Equal(4, nfrPayloads.Count(p => p.TargetSupport == "quote_found"));
        // Every NFR target is still a proposed assumption awaiting a human confirmation (server-owned).
        Assert.All(nfrPayloads.Where(p => p.TargetValue is not null), p => Assert.Equal("proposed_assumption", p.AssumptionStatus));
        Assert.Equal(AiReap.Application.Ai.SourceIdCoverage.Complete, result.SourceIdCoverage!.Status);

        var direct = SrsQualityScorer.Score("replay", Source, Expected, candidate);
        var replayed = SrsQualityScorer.Score("replay", Source, Expected, SrsQualityScorer.FromArtifacts(frs.Concat(nfrs)));
        output.WriteLine(replayed.ToMarkdown());

        Assert.Equal(direct.ToJson(), replayed.ToJson());
    }

    [Fact]
    public async Task Scripted_analysis_through_the_service_persists_clarifications_that_score_as_the_candidate_does()
    {
        var (db, source) = Seed();
        var candidate = GoldenFixture.Candidate("analysis-good");
        var response = new JsonObject
        {
            ["actors"] = new JsonArray("Receptionist", "Clinician", "Administrator"),
            ["capabilities"] = new JsonArray("Book consultation rooms"),
            ["dataElements"] = new JsonArray("Booking"),
            ["notes"] = new JsonArray(),
            ["missingInformation"] = new JsonArray(candidate.Clarifications
                .Select(c => (JsonNode)new JsonObject { ["topic"] = c.Topic, ["question"] = c.Question, ["reason"] = c.Reason }).ToArray())
        }.ToJsonString();
        var ai = new ScriptedAiChatClient(response);

        var result = await Analysis(db, ai).AnalyzeAsync(source.Id);

        Assert.Single(ai.Prompts);
        Assert.Contains(Source, ai.Prompts[0].User);
        Assert.Equal(6, result.ClarificationQuestions.Count);

        var replayed = SrsQualityScorer.Score("replay", Source, Expected,
            SrsQualityScorer.FromArtifacts(Persisted(db, ArtifactType.ClarificationQuestion)));
        output.WriteLine(replayed.ToMarkdown());

        Assert.Equal(new Ratio(3, 3), replayed.Analysis.GapRecall);
        Assert.Equal(new Ratio(2, 2), replayed.Analysis.ContradictionRecallHeuristic);
        Assert.Equal(SrsQualityScorer.Score("replay", Source, Expected, candidate).Analysis, replayed.Analysis, AnalysisComparer.Instance);
    }

    [Fact]
    public async Task Stated_target_without_a_quote_is_persisted_and_scored_not_supported()
    {
        var (db, source) = Seed();
        var perfect = GoldenFixture.Candidate("generation-perfect");
        var candidate = new CandidateOutput
        {
            Requirements =
            [
                perfect.Requirements[0],
                perfect.Requirements.Single(r => r.SourceIds.Contains("NFR-001")) with { Evidence = [] }
            ]
        };
        var ai = new ScriptedAiChatClient(GenerationResponse(candidate));

        await Generation(db, ai).GenerateAsync(source.Id);

        var nfr = JsonSerializer.Deserialize<NonFunctionalRequirementPayload>(Persisted(db, ArtifactType.NonFunctionalRequirement).Single().DataJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("stated", nfr.TargetOrigin);
        Assert.Equal("not_supported", nfr.TargetSupport);
        Assert.Equal("none", nfr.EvidenceStatus);
        Assert.Equal("proposed_assumption", nfr.AssumptionStatus);

        var replayed = SrsQualityScorer.Score("replay", Source, Expected,
            SrsQualityScorer.FromArtifacts(Persisted(db, ArtifactType.FunctionalRequirement).Concat(Persisted(db, ArtifactType.NonFunctionalRequirement))));
        output.WriteLine(replayed.ToMarkdown());

        Assert.Equal(1, replayed.Numbers.StatedTargetsNotSupported);
        Assert.Equal(1, replayed.Citations.ExplicitWithoutVerifiedSrsQuote);
        Assert.Equal(new Ratio(0, 4), replayed.Numbers.StatedTargetAccuracy);
    }

    // AnalysisScores holds lists, so compare its content rather than list references.
    private sealed class AnalysisComparer : IEqualityComparer<AnalysisScores>
    {
        public static readonly AnalysisComparer Instance = new();

        public bool Equals(AnalysisScores? x, AnalysisScores? y) =>
            x is not null && y is not null
            && x.Clarifications == y.Clarifications && x.GapRecall == y.GapRecall && x.ContradictionRecallHeuristic == y.ContradictionRecallHeuristic
            && x.GapsDetected.SequenceEqual(y.GapsDetected) && x.GapsMissed.SequenceEqual(y.GapsMissed)
            && x.ContradictionsDetected.SequenceEqual(y.ContradictionsDetected) && x.ContradictionsMissed.SequenceEqual(y.ContradictionsMissed)
            && x.RedundantClarificationPairs.SequenceEqual(y.RedundantClarificationPairs);

        public int GetHashCode(AnalysisScores obj) => obj.Clarifications;
    }
}
