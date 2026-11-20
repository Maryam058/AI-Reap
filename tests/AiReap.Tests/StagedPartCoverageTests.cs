using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using Xunit;

namespace AiReap.Tests;

// StagedPartCoverage (H1): which IDs an empty staged part defines, when a generated requirement covers an
// ID, and whether an uncovered ID is a missing requirement or an unclassified reference entry. Pure and
// offline: no AI call, no database.
public class StagedPartCoverageTests
{
    private const string Fr120 = "FR-120: The system shall export booking reports as PDF, see FR-250.";
    private const string Fr250 = "FR-250: The system shall export the audit trail to CSV every night.";
    private const string Fr001 = "FR-001: The system shall record every booking with its timestamp.";

    private static StagedCoverageClaim Claim(string[] ids, params string[] quotes) => new(ids, quotes);

    private static StagedPartCoverage.Ledger LedgerFor(string source, params StagedCoverageClaim[] claims)
    {
        var ledger = new StagedPartCoverage.Ledger(source);
        ledger.Add(claims);
        return ledger;
    }

    // ---- what counts as coverage ----------------------------------------------------------------

    [Fact]
    public void An_id_cited_with_a_quote_from_its_own_passage_is_covered()
    {
        var source = $"{Fr120}\n{Fr250}\n";

        var ledger = LedgerFor(source, Claim(["FR-250"], "The system shall export the audit trail to CSV every night."));

        Assert.True(ledger.IsCovered("FR-250"));
        Assert.True(ledger.IsCovered("fr-250")); // case-insensitive
    }

    [Fact]
    public void An_id_cited_elsewhere_whose_own_requirement_is_absent_is_not_covered()
    {
        // FR-120's requirement cites FR-250 (mentioned in its line) but quotes only FR-120's own line.
        var source = $"{Fr120}\n{Fr250}\n";

        var ledger = LedgerFor(source, Claim(["FR-120", "FR-250"], "The system shall export booking reports as PDF, see FR-250."));

        Assert.True(ledger.IsCovered("FR-120"));
        Assert.False(ledger.IsCovered("FR-250"));
        var (missing, unclassified) = ledger.Judge(["FR-250"]);
        Assert.Equal(["FR-250"], missing);          // FR family generated elsewhere: a genuine gap
        Assert.Empty(unclassified);
    }

    [Fact]
    public void An_id_cited_without_any_quote_is_not_covered()
    {
        var ledger = LedgerFor($"{Fr001}\n{Fr250}\n",
            Claim(["FR-001"], "The system shall record every booking with its timestamp."),
            Claim(["FR-250"]));

        Assert.False(ledger.IsCovered("FR-250"));
        Assert.Equal(["FR-250"], ledger.Judge(["FR-250"]).Missing);
    }

    [Fact]
    public void A_quote_that_is_not_in_the_source_covers_nothing()
    {
        var ledger = LedgerFor($"{Fr001}\n{Fr250}\n",
            Claim(["FR-001"], "The system shall record every booking with its timestamp."),
            Claim(["FR-250"], "The system shall export everything to Excel hourly."));

        Assert.False(ledger.IsCovered("FR-250"));
    }

    [Fact]
    public void An_id_defined_twice_is_covered_by_a_quote_from_either_definition()
    {
        // A summary table restates FR-001; the requirement quotes the body line.
        var source = $"{Fr001}\n\n## Summary\n| FR-001 | Booking record kept for every booking made |\n";

        var ledger = LedgerFor(source, Claim(["FR-001"], "The system shall record every booking with its timestamp."));

        Assert.True(ledger.IsCovered("FR-001"));
        Assert.Empty(ledger.Judge(StagedPartCoverage.Candidates("| FR-001 | Booking record kept for every booking made |")).Missing);
    }

    // ---- layout-only lines are not definitions --------------------------------------------------

    [Theory]
    [InlineData("FR-401 Removed booking export ........ 40")]
    [InlineData("FR-401 Removed booking export . . . . 40")]
    [InlineData("3.4 FR-401 Removed booking export\t40")]
    [InlineData("FR-401 Removed booking export… 40")]
    public void A_table_of_contents_line_defines_nothing(string line)
    {
        Assert.True(StagedPartCoverage.IsTableOfContentsLine(line));
        Assert.Empty(StagedPartCoverage.Candidates(line));
    }

    [Theory]
    [InlineData("FR-010: Retries are limited to 3.")]
    [InlineData("FR-010: The export must cover releases 1.2 and 1.3")]
    public void A_requirement_ending_in_a_number_is_not_a_table_of_contents_line(string line)
    {
        Assert.False(StagedPartCoverage.IsTableOfContentsLine(line));
        Assert.Equal(["FR-010"], StagedPartCoverage.Candidates(line));
    }

    [Theory]
    [InlineData("| FR-001 | UC-01 | TC-001 |")]
    [InlineData("| FR-001 | UC-01, UC-02 | TC-001 / TC-002 | Passed |")]
    public void A_traceability_matrix_row_defines_nothing(string row)
    {
        Assert.True(StagedPartCoverage.IsCrossReferenceRow(row));
        Assert.Empty(StagedPartCoverage.Candidates(row));
    }

    [Theory]
    [InlineData("| FR-010 | Exports must include every booking change, see FR-003 |")]
    [InlineData("| FR-010 | UC-04 | The system shall export every booking change nightly |")]
    [InlineData("| FR-010 | Export bookings |")]
    public void A_requirement_table_row_still_defines_its_id(string row)
    {
        Assert.False(StagedPartCoverage.IsCrossReferenceRow(row));
        Assert.Equal(["FR-010"], StagedPartCoverage.Candidates(row));
    }

    [Fact]
    public void A_toc_entry_and_a_body_definition_in_the_same_part_keep_the_body_definition()
    {
        var part = "FR-250 Audit export ........ 12\n\n## 3 Requirements\nFR-250: The system shall export the audit trail.\n";

        Assert.Equal(["FR-250"], StagedPartCoverage.Candidates(part));
    }

    [Fact]
    public void Candidates_use_the_unchanged_classify_rule_and_keep_source_order()
    {
        var part = "Hashing uses SHA-256 throughout.\nFR-002: Second.\n- FR-001: First.\n| ISO-27001 | Information security |\n";

        Assert.Equal(SourceIdCoverage.Classify(part).Defined, StagedPartCoverage.Candidates(part));
        Assert.Equal(["FR-002", "FR-001", "ISO-27001"], StagedPartCoverage.Candidates(part));
    }

    // ---- families decide missing vs unclassified ------------------------------------------------

    [Theory]
    [InlineData("FR-012", "FR")]
    [InlineData("NFR-SEC-01", "NFR-SEC")]
    [InlineData("TA-FR-3", "TA-FR")]
    [InlineData("ISO-27001", "ISO")]
    [InlineData("REV-1.0", "REV")]
    [InlineData("fr-012", "FR")]
    public void A_family_is_the_id_without_its_trailing_number(string id, string family) =>
        Assert.Equal(family, StagedPartCoverage.Family(id));

    private const string Body =
        "FR-001: The system shall record every booking with its timestamp.\n" +
        "UC-01: Receptionist books an appointment for a patient at the desk.\n" +
        "SEC-01: All booking data shall be encrypted at rest with AES.\n";

    private static StagedPartCoverage.Ledger BodyLedger(string appendix) => LedgerFor(Body + appendix,
        Claim(["FR-001"], "The system shall record every booking with its timestamp."),
        Claim(["UC-01"], "Receptionist books an appointment for a patient at the desk."),
        Claim(["SEC-01"], "All booking data shall be encrypted at rest with AES."));

    [Fact]
    public void Standards_glossary_and_revision_tables_are_unclassified_not_missing()
    {
        const string appendix =
            "## Appendix A Standards\n| ISO-27001 | Information security management |\n| IEEE-830 | Recommended practice for SRS |\n" +
            "## Appendix B Glossary\n| AES-256 | Advanced Encryption Standard, 256-bit key |\n" +
            "## Appendix C Revisions\n| REV-1.0 | 2026-01-10 | Initial issue |\n| REV-1.1 | 2026-03-02 | Added exports |\n";
        var ledger = BodyLedger(appendix);

        var (missing, unclassified) = ledger.Judge(StagedPartCoverage.Candidates(appendix));

        Assert.Empty(missing);
        Assert.Equal(["ISO-27001", "IEEE-830", "AES-256", "REV-1.0", "REV-1.1"], unclassified);
    }

    [Fact]
    public void A_real_requirement_mixed_into_a_glossary_is_the_only_missing_id()
    {
        const string part = "## Glossary\n| ISO-27001 | Information security management |\nFR-250: The system shall export the audit trail.\n" +
            "| AES-256 | Advanced Encryption Standard |\n";
        var ledger = BodyLedger(part);

        var (missing, unclassified) = ledger.Judge(StagedPartCoverage.Candidates(part));

        Assert.Equal(["FR-250"], missing);
        Assert.Equal(["ISO-27001", "AES-256"], unclassified);
    }

    [Fact]
    public void A_test_case_appendix_is_unclassified_whatever_its_wording()
    {
        const string part = "## Test cases\nTC-001: The tester must book an appointment and the system must confirm it.\n" +
            "TC-002: The tester shall cancel the booking.\n";

        var (missing, unclassified) = BodyLedger(part).Judge(StagedPartCoverage.Candidates(part));

        Assert.Empty(missing);                       // "must"/"shall" wording is not used to decide
        Assert.Equal(["TC-001", "TC-002"], unclassified);
    }

    [Fact]
    public void A_use_case_or_security_family_generated_elsewhere_is_missing()
    {
        const string part = "UC-07: Patient reschedules an appointment online.\nSEC-07: Sessions shall expire after 15 minutes.\n";

        var (missing, unclassified) = BodyLedger(part).Judge(StagedPartCoverage.Candidates(part));

        Assert.Equal(["UC-07", "SEC-07"], missing);
        Assert.Empty(unclassified);
    }

    [Fact]
    public void A_family_that_exists_only_in_the_empty_part_is_unclassified_known_gap()
    {
        // No PRIV-xx requirement was generated anywhere, so nothing shows the family is requirements.
        const string part = "PRIV-01: Patient data shall be deleted after ten years.\nPRIV-02: Consent shall be recorded.\n";

        var (missing, unclassified) = BodyLedger(part).Judge(StagedPartCoverage.Candidates(part));

        Assert.Empty(missing);
        Assert.Equal(["PRIV-01", "PRIV-02"], unclassified);
    }

    [Fact]
    public void Families_and_ids_compare_case_insensitively()
    {
        // Source IDs are upper-case (SrsIdExtractor); a model may cite them in another case.
        var source = $"{Fr001}\n{Fr250}\n";
        var ledger = LedgerFor(source, Claim(["fr-001"], "The system shall record every booking with its timestamp."));

        Assert.True(ledger.IsCovered("FR-001"));
        Assert.Equal(["FR-250"], ledger.Judge(StagedPartCoverage.Candidates(Fr250)).Missing);
    }

    [Fact]
    public void A_german_source_is_judged_exactly_like_an_english_one()
    {
        const string source =
            "FR-001: Das System muss jede Buchung mit Zeitstempel speichern.\n" +
            "FR-250: Das System muss das Prüfprotokoll jede Nacht als CSV exportieren.\n" +
            "| ISO-27001 | Informationssicherheits-Managementsystem |\n";
        var ledger = LedgerFor(source, Claim(["FR-001"], "Das System muss jede Buchung mit Zeitstempel speichern."));

        var (missing, unclassified) = ledger.Judge(StagedPartCoverage.Candidates(source));

        Assert.Equal(["FR-250"], missing);
        Assert.Equal(["ISO-27001"], unclassified);

        ledger.Add([Claim(["FR-250"], "Das System muss das Prüfprotokoll jede Nacht als CSV exportieren.")]);
        Assert.True(ledger.IsCovered("FR-250"));     // Unicode words (ü) verify like any other
    }
}
