using AiReap.Application.Ai;
using Xunit;

namespace AiReap.Tests;

// P0-4 — SourceIdCoverage: requirement IDs are classified by where they are written (defined at the
// start of a line, or only mentioned in text), never by their prefix, and a defined ID is covered
// only by a non-rejected requirement citing it in sourceIds. Pure: no database, no AI call.
public class SourceIdCoverageTests
{
    private static (IEnumerable<string> SourceIds, bool Rejected) Req(params string[] ids) => (ids, false);
    private static (IEnumerable<string> SourceIds, bool Rejected) Rejected(params string[] ids) => (ids, true);

    // ---- classification by position ---------------------------------------------------------

    [Fact]
    public void Ids_at_the_start_of_a_line_are_defined_and_ids_inside_text_are_mentioned_only()
    {
        const string text =
            "FR-001: Users sign in.\n" +
            "Passwords are hashed with SHA-256 and stored per ISO-27001, as FR-001 requires.\n" +
            "FR-002 Users reset their password (see FR-001 and FR-009).";

        var (defined, mentioned) = SourceIdCoverage.Classify(text);

        Assert.Equal(["FR-001", "FR-002"], defined);
        Assert.Equal(["SHA-256", "ISO-27001", "FR-009"], mentioned);
    }

    [Theory]
    [InlineData("- FR-001 Users sign in.")]
    [InlineData("* FR-001 Users sign in.")]
    [InlineData("• FR-001 Users sign in.")]
    [InlineData("1. FR-001 Users sign in.")]
    [InlineData("2) FR-001 Users sign in.")]
    [InlineData("3.2.1 FR-001 Users sign in.")]
    [InlineData("| FR-001 | Users sign in | High |")]
    [InlineData("|FR-001|Users sign in|")]
    [InlineData("FR-001\tUsers sign in\tHigh")]
    [InlineData("   FR-001: Users sign in.")]
    [InlineData("[FR-001] Users sign in.")]
    [InlineData("**FR-001**: Users sign in.")]
    [InlineData("\r\nFR-001: Users sign in.\r\n")]
    public void Bullets_numbering_table_first_cells_and_indentation_still_define_an_id(string line)
    {
        Assert.Equal(["FR-001"], SourceIdCoverage.Classify("Intro text.\n" + line).Defined);
    }

    [Fact]
    public void Only_a_tables_first_cell_defines_an_id()
    {
        var (defined, mentioned) = SourceIdCoverage.Classify("| FR-001 | Depends on FR-002 |\n| ID | Notes |");
        Assert.Equal(["FR-001"], defined);
        Assert.Equal(["FR-002"], mentioned);
    }

    [Fact]
    public void Nested_ids_are_kept_whole_and_a_longer_token_is_not_a_shorter_id()
    {
        var (defined, mentioned) = SourceIdCoverage.Classify("REQ-3.2.1 Leave is approved.\nREQ-3.2 Leave is requested.\nFR-12 Twelve.\nSee FR-1.");
        Assert.Equal(["REQ-3.2.1", "REQ-3.2", "FR-12"], defined);
        Assert.Equal(["FR-1"], mentioned);
    }

    [Fact]
    public void An_id_defined_once_and_mentioned_elsewhere_is_defined_and_listed_once()
    {
        var (defined, mentioned) = SourceIdCoverage.Classify("See FR-002 below.\nFR-002: Approve leave.\nFR-002 again: no.");
        Assert.Equal(["FR-002"], defined);
        Assert.Empty(mentioned);
    }

    // ---- coverage ----------------------------------------------------------------------------

    private static string TwelveIds() =>
        string.Join("\n", Enumerable.Range(1, 12).Select(i => $"FR-{i:000}: Requirement number {i}."));

    [Fact]
    public void Twelve_defined_ids_ten_cited_reports_exactly_the_other_two()
    {
        var cited = Enumerable.Range(1, 12).Where(i => i is not (4 or 11)).Select(i => Req($"FR-{i:000}"));

        var coverage = SourceIdCoverage.Compute(TwelveIds(), cited);

        Assert.Equal(SourceIdCoverage.Incomplete, coverage.Status);
        Assert.Equal(12, coverage.DefinedCount);
        Assert.Equal(10, coverage.Covered.Count);
        Assert.Equal(["FR-004", "FR-011"], coverage.Uncovered);
        Assert.Empty(coverage.CoveredOnlyByRejected);
    }

    [Fact]
    public void Fr_and_nfr_citations_both_count_and_ids_compare_case_insensitively()
    {
        const string text = "FR-001: Sign in.\nNFR-001: Pages load in 2 seconds.";
        var coverage = SourceIdCoverage.Compute(text, [Req("fr-001"), Req(" NFR-001 ")]); // an edited FR, an NFR

        Assert.Equal(SourceIdCoverage.Complete, coverage.Status);
        Assert.Equal(["FR-001", "NFR-001"], coverage.Covered); // as written in the source
        Assert.Empty(coverage.Uncovered);
    }

    [Fact]
    public void Depends_on_ids_are_not_citations_so_a_dependency_alone_covers_nothing()
    {
        // Compute only ever receives sourceIds; a requirement that merely depends on FR-001 passes none.
        var coverage = SourceIdCoverage.Compute("FR-001: Sign in.\nFR-002: Reset password.", [Req("FR-002"), Req()]);
        Assert.Equal(["FR-001"], coverage.Uncovered);
    }

    [Fact]
    public void Ids_cited_only_by_rejected_requirements_are_uncovered_and_listed_separately()
    {
        var coverage = SourceIdCoverage.Compute(
            "FR-001: Sign in.\nFR-002: Reset password.\nFR-003: Sign out.",
            [Req("FR-001"), Rejected("FR-002"), Rejected("FR-001")]);

        Assert.Equal(SourceIdCoverage.Incomplete, coverage.Status);
        Assert.Equal(["FR-001"], coverage.Covered);
        Assert.Equal(["FR-002", "FR-003"], coverage.Uncovered);
        Assert.Equal(["FR-002"], coverage.CoveredOnlyByRejected);
    }

    [Fact]
    public void Mentioned_only_ids_are_never_counted_as_missing_or_covered()
    {
        var coverage = SourceIdCoverage.Compute("FR-001: Hash with SHA-256.", [Req("FR-001", "SHA-256")]);
        Assert.Equal(SourceIdCoverage.Complete, coverage.Status);
        Assert.Equal(1, coverage.DefinedCount);
        Assert.Equal(["SHA-256"], coverage.MentionedOnly);
        Assert.DoesNotContain("SHA-256", coverage.Covered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Employees request leave. Managers approve it.")]
    [InlineData("Passwords use SHA-256 per ISO-27001.")]
    public void A_source_without_defined_ids_is_not_applicable_never_complete(string? text)
    {
        var coverage = SourceIdCoverage.Compute(text, [Req("FR-001")]);
        Assert.Equal(SourceIdCoverage.NotApplicable, coverage.Status);
        Assert.Equal(0, coverage.DefinedCount);
        Assert.Empty(coverage.Uncovered);
        Assert.Empty(coverage.Covered);
    }

    [Fact]
    public void Nothing_saved_yet_leaves_every_defined_id_uncovered()
    {
        var coverage = SourceIdCoverage.Compute(TwelveIds(), []);
        Assert.Equal(12, coverage.Uncovered.Count);
        Assert.Equal(SourceIdCoverage.Incomplete, coverage.Status);
    }
}
