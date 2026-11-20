using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Tests.SrsEval;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Phase 0 / T2 — the scorer itself, against hand-written candidate outputs (Fixtures/Srs/candidates)
// whose correct metrics are worked out by hand. Every expected value below is exact. Reports go to
// xUnit test output only. Offline: no AI provider, no SQL Server, no network.
[Trait("Category", "Phase0")]
public class SrsQualityScorerTests(ITestOutputHelper output)
{
    private static string Source => GoldenFixture.Source(GoldenFixture.ClinicBooking);
    private static FixtureExpectation Expected => GoldenFixture.Expected(GoldenFixture.ClinicBooking);

    private SrsQualityReport Score(string candidate)
    {
        var report = SrsQualityScorer.Score($"{GoldenFixture.ClinicBooking} / {candidate}", Source, Expected, GoldenFixture.Candidate(candidate));
        output.WriteLine(report.ToMarkdown());
        output.WriteLine(report.ToJson());
        return report;
    }

    private static void AssertRatio(int numerator, int denominator, Ratio actual) => Assert.Equal(new Ratio(numerator, denominator), actual);

    [Fact]
    public void Perfect_candidate_scores_full_recall_precision_and_no_violations()
    {
        var r = Score("generation-perfect");

        Assert.Equal(16, r.Requirements.Expected);
        AssertRatio(16, 16, r.Requirements.Recall);
        AssertRatio(16, 16, r.Requirements.GroundedRecall);
        Assert.Equal(0, r.Requirements.KeywordOnlyMatches);
        AssertRatio(11, 11, r.Requirements.FrRecall);
        AssertRatio(5, 5, r.Requirements.NfrRecall);
        AssertRatio(16, 16, r.Requirements.Precision);
        Assert.Equal(0, r.Requirements.UnmergedDuplicates + r.Requirements.BackgroundPromoted + r.Requirements.Unmatched);

        AssertRatio(17, 17, r.Citations.QuotesVerified);
        AssertRatio(16, 16, r.Citations.ItemsWithVerifiedSrsQuote);
        AssertRatio(17, 17, r.Citations.CitedSourceIdsDefined);
        Assert.Equal(0, r.Citations.ExplicitWithoutVerifiedSrsQuote);
        Assert.Equal(SourceIdCoverage.Complete, r.Citations.CoverageStatus);

        Assert.Equal(0, r.Numbers.ItemsWithUnsupportedNumbers);
        Assert.Equal(0, r.Numbers.ItemsWithNumbersNotInSource);
        Assert.Equal(0, r.Numbers.StatedTargetsNotSupported);
        AssertRatio(4, 4, r.Numbers.StatedTargetAccuracy);

        Assert.Equal([new DuplicateResult("D1", "R-FR-004", true, 0, true), new DuplicateResult("D2", "R-FR-002", true, 0, true)], r.Duplicates);
        Assert.All(r.Items, i => Assert.Equal(SrsQualityScorer.SourceIdMatch, i.Outcome));
    }

    [Fact]
    public void Lossy_candidate_reports_the_exact_missing_requirements_and_uncovered_ids()
    {
        var r = Score("generation-lossy");

        Assert.Equal(13, r.Requirements.Generated);
        AssertRatio(13, 16, r.Requirements.Recall);
        AssertRatio(12, 16, r.Requirements.GroundedRecall);
        Assert.Equal(1, r.Requirements.KeywordOnlyMatches);
        AssertRatio(9, 11, r.Requirements.FrRecall);
        AssertRatio(4, 5, r.Requirements.NfrRecall);
        AssertRatio(13, 13, r.Requirements.Precision);
        Assert.Equal(["R-FR-005", "R-FR-010", "R-NFR-002"], r.Requirements.Missing);

        AssertRatio(13, 13, r.Citations.QuotesVerified);
        AssertRatio(12, 13, r.Citations.ItemsWithVerifiedSrsQuote);
        AssertRatio(13, 13, r.Citations.CitedSourceIdsDefined);
        Assert.Equal(SourceIdCoverage.Incomplete, r.Citations.CoverageStatus);
        Assert.Equal(["FR-005", "FR-010", "FR-012", "NFR-002"], r.Citations.UncoveredSourceIds);
        AssertRatio(3, 4, r.Numbers.StatedTargetAccuracy);
    }

    [Fact]
    public void Keyword_only_match_counts_for_recall_but_never_as_evidence_or_a_citation()
    {
        var r = Score("generation-lossy");
        var deposit = r.Items.Single(i => i.MatchedKey == "R-FR-012");

        Assert.Equal(SrsQualityScorer.KeywordOnlyMatch, deposit.Outcome);
        Assert.Equal("none", deposit.EvidenceStatus);
        // Counted in recall (13) but not in grounded recall (12); its requirement ID stays uncovered.
        Assert.Equal(r.Requirements.Recall.Numerator - 1, r.Requirements.GroundedRecall.Numerator);
        Assert.Contains("FR-012", r.Citations.UncoveredSourceIds);
        Assert.Equal(r.Requirements.Generated - 1, r.Citations.ItemsWithVerifiedSrsQuote.Numerator);
    }

    [Fact]
    public void Inventing_candidate_counts_invented_numbers_background_promotion_and_unsupported_explicit_items()
    {
        var r = Score("generation-inventing");

        Assert.Equal(6, r.Requirements.Generated);
        AssertRatio(3, 16, r.Requirements.Recall);
        AssertRatio(2, 16, r.Requirements.GroundedRecall);
        Assert.Equal(1, r.Requirements.KeywordOnlyMatches);
        AssertRatio(3, 6, r.Requirements.Precision);
        Assert.Equal(1, r.Requirements.BackgroundPromoted);
        Assert.Equal(2, r.Requirements.Unmatched);

        AssertRatio(3, 4, r.Citations.QuotesVerified);
        AssertRatio(3, 6, r.Citations.ItemsWithVerifiedSrsQuote);
        AssertRatio(2, 3, r.Citations.CitedSourceIdsDefined);
        Assert.Equal(2, r.Citations.ExplicitWithoutVerifiedSrsQuote);

        Assert.Equal(2, r.Numbers.ItemsWithUnsupportedNumbers);
        Assert.Equal(2, r.Numbers.ItemsWithNumbersNotInSource);
        Assert.Equal(1, r.Numbers.StatedTargetsNotSupported);
        AssertRatio(0, 4, r.Numbers.StatedTargetAccuracy);

        Assert.Equal(["15"], r.Items[0].UnsupportedNumbers);
        Assert.Equal(RequirementEvidence.NotSupported, r.Items[1].TargetSupport);
        Assert.Equal(SrsQualityScorer.BackgroundPromoted, r.Items[2].Outcome);
        Assert.Equal("N-01", r.Items[2].MatchedKey);
        Assert.Equal("unverified", r.Items[3].EvidenceStatus);
    }

    [Fact]
    public void Duplicating_candidate_reports_unmerged_D1_and_D2()
    {
        var r = Score("generation-duplicating");

        Assert.Equal(18, r.Requirements.Generated);
        AssertRatio(16, 16, r.Requirements.Recall);
        AssertRatio(16, 18, r.Requirements.Precision);
        Assert.Equal(2, r.Requirements.UnmergedDuplicates);
        AssertRatio(18, 18, r.Citations.QuotesVerified);
        AssertRatio(17, 17, r.Citations.CitedSourceIdsDefined);
        Assert.Equal(SourceIdCoverage.Complete, r.Citations.CoverageStatus);
        Assert.Equal([new DuplicateResult("D1", "R-FR-004", false, 1, false), new DuplicateResult("D2", "R-FR-002", false, 1, true)], r.Duplicates);

        var summary = r.Items.Single(i => i.Title == "No double booking (summary)");
        Assert.Equal(SrsQualityScorer.Duplicate, summary.Outcome);
        Assert.Equal("R-FR-002", summary.MatchedKey);
    }

    [Fact]
    public void Analysis_good_detects_every_planted_gap_and_contradiction()
    {
        var r = Score("analysis-good");

        Assert.Equal(6, r.Analysis.Clarifications);
        AssertRatio(3, 3, r.Analysis.GapRecall);
        AssertRatio(2, 2, r.Analysis.ContradictionRecallHeuristic);
        Assert.Empty(r.Analysis.RedundantClarificationPairs);
    }

    [Fact]
    public void Analysis_poor_reports_missed_gaps_missed_contradictions_and_a_redundant_pair()
    {
        var r = Score("analysis-poor");

        Assert.Equal(4, r.Analysis.Clarifications);
        AssertRatio(1, 3, r.Analysis.GapRecall);
        Assert.Equal(["G2", "G3"], r.Analysis.GapsMissed);
        AssertRatio(0, 2, r.Analysis.ContradictionRecallHeuristic);
        Assert.Equal(["C1", "C2"], r.Analysis.ContradictionsMissed);
        Assert.Equal(["1~2"], r.Analysis.RedundantClarificationPairs);
    }

    [Fact]
    public void Unnumbered_fixture_matches_by_verified_quote_and_coverage_is_not_applicable()
    {
        // The perfect candidate with every source ID removed (from items and quotes), scored on the
        // unnumbered fixture: the same requirements are found through verified quotes alone.
        var perfect = GoldenFixture.Candidate("generation-perfect");
        var withoutIds = perfect with
        {
            Requirements = perfect.Requirements.Select(c => c with
            {
                SourceIds = [],
                Evidence = c.Evidence.Select(q => q with { SourceId = null }).ToList()
            }).ToList()
        };
        var expected = GoldenFixture.UnnumberedExpected(Expected);
        var r = SrsQualityScorer.Score("clinic-booking (unnumbered)", GoldenFixture.Unnumbered(Source), expected, withoutIds);
        output.WriteLine(r.ToMarkdown());

        AssertRatio(16, 16, r.Requirements.GroundedRecall);
        Assert.All(r.Items, i => Assert.Equal(SrsQualityScorer.VerifiedQuoteMatch, i.Outcome));
        Assert.Equal(SourceIdCoverage.NotApplicable, r.Citations.CoverageStatus);
        AssertRatio(0, 0, r.Citations.CitedSourceIdsDefined);
    }

    [Fact]
    public void Report_is_byte_identical_across_runs()
    {
        foreach (var candidate in new[] { "generation-perfect", "generation-lossy", "generation-inventing", "generation-duplicating", "analysis-good", "analysis-poor" })
        {
            var first = SrsQualityScorer.Score(candidate, Source, Expected, GoldenFixture.Candidate(candidate));
            var second = SrsQualityScorer.Score(candidate, Source, Expected, GoldenFixture.Candidate(candidate));

            Assert.Equal(first.ToJson(), second.ToJson());
            Assert.Equal(first.ToMarkdown(), second.ToMarkdown());
        }
    }
}
