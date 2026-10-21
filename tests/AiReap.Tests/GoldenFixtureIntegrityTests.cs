using AiReap.Application.Ai;
using AiReap.Tests.SrsEval;
using Xunit;

namespace AiReap.Tests;

// Phase 0 / T0 — the golden fixtures and their expectations agree with each other, checked with the
// product's own deterministic code (SourceEvidenceIndex, SourceIdCoverage, SourceChunker). A fixture
// edit that silently invalidates an expectation fails here, before any metric is trusted.
// Offline: no AI provider, no SQL Server, no network.
[Trait("Category", "Phase0")]
public class GoldenFixtureIntegrityTests
{
    public static TheoryData<string> Fixtures => new() { GoldenFixture.ClinicBooking, GoldenFixture.VagueRequest };

    private static IEnumerable<string> AllAnchors(FixtureExpectation e) =>
        e.Requirements.SelectMany(r => r.AnchorQuotes)
            .Concat(e.NonRequirements.Select(n => n.AnchorQuote))
            .Concat(e.Contradictions.SelectMany(c => c.AnchorQuotes))
            .Concat(e.Gaps.SelectMany(g => g.AnchorQuotes));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_anchor_quote_is_verified_in_its_fixture(string name)
    {
        var index = SourceEvidenceIndex.Build(GoldenFixture.Source(name));
        var failures = AllAnchors(GoldenFixture.Expected(name))
            .Select(a => (Anchor: a, Check: index.Check(a)))
            .Where(x => x.Check.Status != EvidenceStatus.Verified)
            .Select(x => $"{x.Anchor} -> {x.Check.Status} {x.Check.Reason}")
            .ToList();

        Assert.Empty(failures);
    }

    [Fact]
    public void Every_anchor_quote_is_verified_in_the_derived_unnumbered_fixture()
    {
        var expected = GoldenFixture.UnnumberedExpected(GoldenFixture.Expected(GoldenFixture.ClinicBooking));
        var index = SourceEvidenceIndex.Build(GoldenFixture.Unnumbered(GoldenFixture.Source(GoldenFixture.ClinicBooking)));

        Assert.All(AllAnchors(expected), a => Assert.Equal(EvidenceStatus.Verified, index.Check(a).Status));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Expected_source_ids_equal_the_extractor_classification(string name)
    {
        var expected = GoldenFixture.Expected(name).SourceIds;
        var (defined, mentionedOnly) = SourceIdCoverage.Classify(GoldenFixture.Source(name));

        Assert.Equal(expected.Defined, defined);
        Assert.Equal(expected.MentionedOnly, mentionedOnly);
    }

    [Fact]
    public void Unnumbered_fixture_has_no_defined_ids_and_coverage_is_not_applicable()
    {
        var unnumbered = GoldenFixture.Unnumbered(GoldenFixture.Source(GoldenFixture.ClinicBooking));
        var (defined, mentionedOnly) = SourceIdCoverage.Classify(unnumbered);

        Assert.Empty(defined);
        Assert.Equal(["SHA-256"], mentionedOnly);
        Assert.Equal(SourceIdCoverage.NotApplicable, SourceIdCoverage.Compute(unnumbered, []).Status);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Expectations_are_internally_consistent(string name)
    {
        var e = GoldenFixture.Expected(name);
        Assert.True(e.Synthetic);

        var keys = e.Requirements.Select(r => r.Key).Concat(e.AllowedDerived.Select(d => d.Key)).Concat(e.NonRequirements.Select(n => n.Key))
            .Concat(e.Contradictions.Select(c => c.Key)).Concat(e.Gaps.Select(g => g.Key)).Concat(e.Duplicates.Select(d => d.Key)).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        // Every requirement cites only defined IDs, and every defined ID belongs to exactly one requirement.
        var cited = e.Requirements.SelectMany(r => r.SourceIds).ToList();
        Assert.Equal(cited.Count, cited.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(e.SourceIds.Defined.Order(StringComparer.Ordinal), cited.Order(StringComparer.Ordinal));

        Assert.All(e.Requirements, r => Assert.Contains(r.Kind, new[] { "FR", "NFR" }));
        Assert.All(e.Requirements, r => Assert.NotEmpty(r.AnchorQuotes));
        Assert.All(e.Duplicates, d => Assert.Contains(e.Requirements, r => r.Key == d.Requirement));
        Assert.All(e.Contradictions.Concat(e.Gaps), p => Assert.NotEmpty(p.KeywordGroups));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_planted_keyword_group_occurs_in_the_source(string name)
    {
        // A planted contradiction or gap must be detectable from the source's own words: each keyword
        // group has at least one member written in the source (C1: both "24 hours" and "48 hours").
        var source = GoldenFixture.Source(name);
        var e = GoldenFixture.Expected(name);

        Assert.All(e.Contradictions, c => Assert.All(c.KeywordGroups, g => Assert.Contains(g, k => SrsQualityScorer.ContainsPhrase(source, k))));
        Assert.All(e.Gaps, gap => Assert.Contains(gap.KeywordGroups[0], k => SrsQualityScorer.ContainsPhrase(source, k)));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Nonrequirement_anchors_do_not_overlap_requirement_anchors(string name)
    {
        var e = GoldenFixture.Expected(name);
        foreach (var n in e.NonRequirements)
        {
            Assert.DoesNotContain(e.Requirements.SelectMany(r => r.AnchorQuotes),
                a => SrsQualityScorer.ContainsPhrase(a, n.AnchorQuote) || SrsQualityScorer.ContainsPhrase(n.AnchorQuote, a));
        }
    }

    [Fact]
    public void Large_fixture_split_reconstructs_every_non_blank_line_exactly_and_in_order()
    {
        // The only transformations allowed: line endings are "\n" (normalized when the fixture is read)
        // and blank lines are not compared (SourceChunker drops blank-line separators). No line may be
        // trimmed, merged, split or reordered. Preconditions that keep that true: no fixture line has
        // trailing whitespace, and every line fits a part on its own (so no sentence-level split).
        const int budget = 250;
        var source = GoldenFixture.Large(GoldenFixture.Source(GoldenFixture.ClinicBooking));
        var original = source.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.All(original, l => Assert.Equal(l.TrimEnd(), l));
        Assert.All(original, l => Assert.True(PromptBudgetReducer.EstimateTokens(l) < budget, l));

        var parts = SourceChunker.Split(source, budget);
        var reassembled = parts.SelectMany(p => p.Split('\n')).Where(l => l.Trim().Length > 0).ToList();

        Assert.True(parts.Count >= 3, $"expected at least 3 parts, got {parts.Count}");
        Assert.Equal(original, reassembled);
    }

    [Fact]
    public void Large_fixture_puts_the_two_halves_of_contradiction_C1_in_different_parts()
    {
        var source = GoldenFixture.Large(GoldenFixture.Source(GoldenFixture.ClinicBooking));
        var c1 = GoldenFixture.Expected(GoldenFixture.ClinicBooking).Contradictions.Single(c => c.Key == "C1");
        var parts = SourceChunker.Split(source, 250);

        int PartOf(string anchor) => parts.Select((p, i) => (p, i)).Single(x => SrsQualityScorer.ContainsPhrase(x.p, anchor)).i;

        Assert.NotEqual(PartOf(c1.AnchorQuotes[0]), PartOf(c1.AnchorQuotes[1]));
    }
}
