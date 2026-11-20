using AiReap.Application.Ai;
using Xunit;

namespace AiReap.Tests;

// SourceEvidence / SourceEvidenceIndex: deterministic quote checking against a requirement source.
// "Verified" means only that the quoted words are written in the source (at the cited ID's passage,
// when one is cited) - never that a requirement is correct. Similarity never verifies; a quote that
// is not found as written is Unverified with a reason, and an item with any unverified quote is not
// verified.
public class SourceEvidenceTests
{
    private const string Srs = """
        1 Introduction
        This document describes the leave management system.

        2 Functional requirements
        FR-001 Employees shall submit leave requests through the portal.
        FR-002 A manager must approve or reject each request within 3 working days.
        Approval is recorded in the audit log.
        FR-003 The system shall not allow an employee to approve their own request.
        FR-004 The system shall log the event. See FR-002 for approval timing.

        3 Expenses
        FR-005 The system shall log the event.

        4 Security
        NFR-001 Accounts shall lock after 5 failed login attempts.
        The authen-
        tication service must respond within 2 seconds.
        """;

    private static readonly SourceEvidenceIndex Index = SourceEvidenceIndex.Build(Srs);

    private static void AssertVerified(QuoteCheck check, EvidenceMatchMode mode = EvidenceMatchMode.Strict)
    {
        Assert.Equal(EvidenceStatus.Verified, check.Status);
        Assert.Null(check.Reason);
        Assert.Equal(mode, check.MatchMode);
    }

    private static void AssertUnverified(QuoteCheck check, EvidenceReason? reason = null)
    {
        Assert.Equal(EvidenceStatus.Unverified, check.Status);
        Assert.NotNull(check.Reason);
        if (reason is not null) Assert.Equal(reason, check.Reason);
    }

    // ---- valid quotations ----------------------------------------------------------------------

    [Fact]
    public void Exact_quote_is_verified_strictly()
    {
        var check = Index.Check("Employees shall submit leave requests through the portal.");

        AssertVerified(check);
        Assert.Equal(1, check.MatchCount);
    }

    [Fact]
    public void Formatting_differences_alone_do_not_prevent_verification()
    {
        AssertVerified(Index.Check("“EMPLOYEES  shall submit\nleave   requests through the portal”"));

        // Soft hyphen, PDF ligature (U+FB01 "fi") and non-breaking space in the source.
        var pdf = SourceEvidenceIndex.Build("Users shall con­ﬁrm the request form before sending it.");
        AssertVerified(pdf.Check("users shall confirm the request form"));
    }

    [Fact]
    public void Word_hyphenated_across_a_line_break_is_rejoined()
    {
        AssertVerified(Index.Check("The authentication service must respond within 2 seconds"));
    }

    [Fact]
    public void Contiguous_partial_quotation_of_four_or_more_words_is_verified()
    {
        AssertVerified(Index.Check("submit leave requests through"));
    }

    [Fact]
    public void Quote_spanning_lines_and_paragraphs_is_verified_against_the_whole_source()
    {
        AssertVerified(Index.Check("within 3 working days. Approval is recorded in the audit log"));
        AssertVerified(Index.Check("Approval is recorded in the audit log. FR-003 The system shall not"));
    }

    [Fact]
    public void Ellipsis_fragments_in_order_and_close_together_are_verified()
    {
        AssertVerified(Index.Check("A manager must approve … recorded in the audit log"));
        AssertVerified(Index.Check("A manager must approve ... recorded in the audit log"));
    }

    // ---- partial, short and malformed quotations ------------------------------------------------

    [Fact]
    public void Quote_shorter_than_the_minimum_is_too_short()
    {
        AssertUnverified(Index.Check("the system"), EvidenceReason.TooShort);
        AssertUnverified(Index.Check("Employees shall submit"), EvidenceReason.TooShort);
    }

    [Fact]
    public void Real_start_with_an_invented_continuation_is_not_verified()
    {
        AssertUnverified(Index.Check("Employees shall submit leave requests by fax"), EvidenceReason.Altered);
    }

    [Fact]
    public void Ellipsis_fragments_out_of_order_too_far_apart_or_too_short_are_not_verified()
    {
        AssertUnverified(Index.Check("recorded in the audit log ... A manager must approve"));

        var far = SourceEvidenceIndex.Build(
            "Alpha beta gamma delta.\n" + string.Join(" ", Enumerable.Repeat("filler", 120)) + "\nEpsilon zeta eta theta.");
        AssertUnverified(far.Check("Alpha beta ... eta theta"));
        AssertVerified(SourceEvidenceIndex.Build("Alpha beta gamma delta. Epsilon zeta eta theta.").Check("Alpha beta ... eta theta"));

        AssertUnverified(Index.Check("Employees ... through the portal now"), EvidenceReason.TooShort);
    }

    // ---- ellipsis that skips a negation or material qualifier ----------------------------------

    [Theory]
    [InlineData("The system shall … allow an employee to approve their own request")]
    [InlineData("The system shall ... allow an employee to approve their own request")]
    [InlineData("The system shall . . . allow an employee to approve their own request")]
    public void Ellipsis_that_skips_the_negation_of_the_reported_example_is_altered(string quote)
    {
        AssertUnverified(Index.Check(quote), EvidenceReason.Altered);
        AssertUnverified(Index.Check(quote, "FR-003"), EvidenceReason.Altered);

        var standalone = SourceEvidenceIndex.Build("The system shall not allow an employee to approve their own request");
        AssertUnverified(standalone.Check(quote), EvidenceReason.Altered);
    }

    [Theory]
    [InlineData("Managers shall never approve requests submitted after the deadline.", "Managers shall ... approve requests submitted after the deadline")]
    [InlineData("The portal shall send no reminder emails to inactive users.", "The portal shall send ... reminder emails to inactive users")]
    [InlineData("The users cannot delete approved requests.", "The users ... delete approved requests")]
    [InlineData("The employees can't approve their own leave requests.", "The employees ... approve their own leave requests")]
    [InlineData("Administrators may close tickets without notifying the requester.", "Administrators may close tickets ... notifying the requester")]
    [InlineData("Guests must not, under any circumstances, view salary data.", "Guests must ... under any circumstances, view salary data")]
    public void Ellipsis_that_skips_a_negation_is_altered(string source, string quote)
    {
        AssertUnverified(SourceEvidenceIndex.Build(source).Check(quote), EvidenceReason.Altered);
    }

    [Theory]
    [InlineData("Managers may approve only requests from their own team.", "Managers may approve ... requests from their own team")]
    [InlineData("All staff may export reports except contractors on probation.", "All staff may export reports ... contractors on probation")]
    [InlineData("Accounts shall be locked unless the administrator grants an exemption.", "Accounts shall be locked ... the administrator grants an exemption")]
    [InlineData("Passwords must contain at least 12 characters in total.", "Passwords must contain ... 12 characters in total")]
    [InlineData("Uploads shall be at most 10 MB per file.", "Uploads shall be ... 10 MB per file")]
    [InlineData("The client shall retry more than 3 times before failing.", "The client shall retry ... 3 times before failing")]
    [InlineData("The system shall archive the record if the case is closed.", "The system shall archive the record ... the case is closed")]
    public void Ellipsis_that_skips_a_material_qualifier_is_altered(string source, string quote)
    {
        AssertUnverified(SourceEvidenceIndex.Build(source).Check(quote), EvidenceReason.Altered);
    }

    [Fact]
    public void Ellipsis_that_omits_only_non_material_text_is_still_verified()
    {
        // The skipped text ("for every submitted leave request,") narrows nothing the quote asserts.
        var index = SourceEvidenceIndex.Build(
            "The system shall, for every submitted leave request, send a confirmation email to the employee.");
        AssertVerified(index.Check("The system shall ... send a confirmation email to the employee"));

        // A negation kept inside a fragment is quoted, not skipped.
        AssertVerified(Index.Check("The system shall not … approve their own request"));
    }

    [Fact]
    public void Ellipsis_skipping_a_negation_in_one_place_is_verified_where_another_occurrence_skips_none()
    {
        var index = SourceEvidenceIndex.Build("Clerks shall not edit closed cases. Clerks shall review and edit open cases.");

        var check = index.Check("Clerks shall ... edit open cases");
        AssertVerified(check);
        Assert.Equal(1, check.MatchCount);
    }

    // ---- altered and fabricated quotations: similarity never verifies --------------------------

    [Fact]
    public void Changed_number_is_altered_not_verified()
    {
        AssertUnverified(Index.Check("A manager must approve or reject each request within 5 working days"), EvidenceReason.Altered);
    }

    [Fact]
    public void Changed_unit_is_not_verified()
    {
        AssertUnverified(Index.Check("The authentication service must respond within 2 minutes"), EvidenceReason.Altered);
    }

    [Fact]
    public void Dropped_negation_is_altered_not_verified()
    {
        AssertUnverified(Index.Check("The system shall allow an employee to approve their own request"), EvidenceReason.Altered);
    }

    [Fact]
    public void Reordered_words_are_altered_not_verified()
    {
        AssertUnverified(Index.Check("Employees shall submit through the portal leave requests"), EvidenceReason.Altered);
    }

    [Fact]
    public void Paraphrase_is_not_verified()
    {
        AssertUnverified(Index.Check("Staff members are able to file their absence requests online"), EvidenceReason.NotFound);
    }

    [Fact]
    public void Fabricated_quote_is_not_found()
    {
        AssertUnverified(Index.Check("Managers can export payroll reports to Excel every week"), EvidenceReason.NotFound);
    }

    [Fact]
    public void Text_that_exists_only_in_the_prompt_is_not_found()
    {
        AssertUnverified(Index.Check("[... 3 passage(s) omitted to fit the AI provider's request budget ...]"));
        AssertUnverified(Index.Check("this requirement source is too large for a single request to the AI provider"), EvidenceReason.NotFound);
    }

    // ---- requirement IDs, repeated requirements, ambiguity ------------------------------------

    [Fact]
    public void Repeated_requirement_is_verified_and_its_occurrences_counted()
    {
        var check = Index.Check("The system shall log the event.");

        AssertVerified(check);
        Assert.Equal(2, check.MatchCount);
    }

    [Fact]
    public void Ambiguous_quote_is_tied_to_the_cited_id_passage()
    {
        AssertVerified(Index.Check("The system shall log the event.", "FR-005"));
        AssertVerified(Index.Check("The system shall log the event.", "FR-004"));

        var wrong = Index.Check("The system shall log the event.", "FR-001");
        AssertUnverified(wrong, EvidenceReason.NotAtCitedId);
        Assert.Equal(2, wrong.MatchCount);
    }

    [Fact]
    public void Passage_of_an_id_runs_to_the_next_requirement_or_heading()
    {
        AssertVerified(Index.Check("Approval is recorded in the audit log", "FR-002"));
        AssertUnverified(Index.Check("Approval is recorded in the audit log", "FR-003"), EvidenceReason.NotAtCitedId);
    }

    [Fact]
    public void Unknown_source_id_is_reported()
    {
        AssertUnverified(Index.Check("Employees shall submit leave requests", "FR-999"), EvidenceReason.UnknownSourceId);
    }

    [Fact]
    public void Mention_of_a_defined_id_inside_another_requirement_is_not_its_passage()
    {
        // FR-004's text says "See FR-002", but FR-002 is defined elsewhere: FR-004's text is not FR-002's evidence.
        AssertUnverified(Index.Check("The system shall log the event.", "FR-002"), EvidenceReason.NotAtCitedId);
    }

    [Fact]
    public void Id_that_is_only_mentioned_uses_the_mentioning_lines()
    {
        var index = SourceEvidenceIndex.Build("The approval rules in BR-010 apply to all managers when they review requests.");

        AssertVerified(index.Check("apply to all managers when", "BR-010"));
    }

    // ---- tables --------------------------------------------------------------------------------

    [Fact]
    public void Table_row_evidence_is_verified_in_cell_per_line_and_pipe_layouts()
    {
        var index = SourceEvidenceIndex.Build(
            "Role\nAdmin\nYes\nFull access to every module\n| Manager | Yes | Approves team requests |");

        AssertVerified(index.Check("Admin Yes Full access to every module"));
        AssertVerified(index.Check("Manager | Yes | Approves team requests"));
        AssertUnverified(index.Check("Yes"), EvidenceReason.TooShort);
        AssertUnverified(index.Check("Admin: Yes"), EvidenceReason.TooShort);
    }

    // ---- spacing-tolerant matching for merged PDF words ----------------------------------------

    [Fact]
    public void Merged_source_words_are_matched_only_after_strict_matching_fails()
    {
        var merged = SourceEvidenceIndex.Build("Thesystemshall lock the account after five failed attempts.");
        AssertVerified(merged.Check("The system shall lock the account after five failed attempts"), EvidenceMatchMode.SpacingTolerant);

        // The same quote against normally spaced text matches strictly.
        AssertVerified(SourceEvidenceIndex.Build("The system shall lock the account after five failed attempts.")
            .Check("The system shall lock the account after five failed attempts"));
    }

    [Fact]
    public void Spacing_tolerance_never_turns_a_short_word_into_two_words()
    {
        var index = SourceEvidenceIndex.Build("Each island owner records the boundary.");
        AssertUnverified(index.Check("Each is land owner records the boundary"));

        var cannot = SourceEvidenceIndex.Build("Users cannot delete approved requests.");
        AssertUnverified(cannot.Check("Users can not delete approved requests"));
    }

    [Fact]
    public void Spacing_tolerance_never_joins_two_numbers()
    {
        var index = SourceEvidenceIndex.Build("Accounts lock after 15 days of inactivity.");

        AssertUnverified(index.Check("Accounts lock after 1 5 days"));
    }

    [Fact]
    public void Spacing_tolerance_never_accepts_merged_quote_words_or_split_source_words()
    {
        AssertUnverified(SourceEvidenceIndex.Build("The system shall lock the account.").Check("Thesystemshall lock the account"));
        AssertUnverified(SourceEvidenceIndex.Build("The sys tem shall lock the account.").Check("The system shall lock the account"));
    }

    // ---- empty or unavailable input ------------------------------------------------------------

    [Fact]
    public void Empty_or_missing_source_never_verifies()
    {
        AssertUnverified(SourceEvidenceIndex.Build("").Check("Employees shall submit leave requests"), EvidenceReason.SourceUnavailable);
        AssertUnverified(SourceEvidenceIndex.Build(null).Check("Employees shall submit leave requests"), EvidenceReason.SourceUnavailable);
        AssertUnverified(SourceEvidenceIndex.Build("  \n\t ").Check("Employees shall submit leave requests"), EvidenceReason.SourceUnavailable);
        Assert.False(SourceEvidenceIndex.Build(" ").HasSource);
    }

    [Fact]
    public void Missing_quote_is_none()
    {
        foreach (var quote in new[] { null, "", "   " })
        {
            var check = Index.Check(quote, "FR-001");
            Assert.Equal(EvidenceStatus.None, check.Status);
            Assert.Null(check.Reason);
            Assert.Equal(EvidenceMatchMode.None, check.MatchMode);
        }
    }

    // ---- item-level status ---------------------------------------------------------------------

    [Fact]
    public void Item_is_verified_only_when_every_quote_given_is_verified()
    {
        var all = Index.Assess([("Employees shall submit leave requests", "FR-001"), ("recorded in the audit log", "FR-002")]);
        Assert.Equal(EvidenceStatus.Verified, all.Status);

        var mixed = Index.Assess([("Employees shall submit leave requests", "FR-001"), ("Managers can export payroll reports to Excel", null)]);
        Assert.Equal(EvidenceStatus.Unverified, mixed.Status);
        Assert.Equal(2, mixed.Quotes.Count);
        Assert.Equal(EvidenceStatus.Verified, mixed.Quotes[0].Status);
        Assert.Equal(EvidenceReason.NotFound, mixed.Quotes[1].Reason);

        Assert.Equal(EvidenceStatus.None, Index.Assess([]).Status);
        Assert.Equal(EvidenceStatus.None, Index.Assess(null).Status);
        Assert.Equal(EvidenceStatus.None, Index.Assess([(null, "FR-001"), ("  ", null)]).Status);

        // A blank entry next to a real quote does not count against it.
        Assert.Equal(EvidenceStatus.Verified, Index.Assess([("Employees shall submit leave requests", null), (null, null)]).Status);
    }

    // ---- what "verified" does not mean ---------------------------------------------------------

    [Fact]
    public void Matching_quote_does_not_show_that_the_requirement_figures_came_from_the_source()
    {
        const string quote = "Accounts shall lock after 5 failed login attempts";
        AssertVerified(Index.Check(quote, "NFR-001")); // the quote is real...

        // ...but the requirement claims more than it says.
        Assert.Equal(new[] { "30 minutes" }, SourceEvidence.NumbersMissingFromEvidence("Accounts lock for 30 minutes after 5 failed attempts", [quote]));
        Assert.Equal(new[] { "2 seconds" }, SourceEvidence.NumbersMissingFromEvidence("The service must respond within 2 seconds", ["service must respond within 2 minutes"]));
    }

    [Fact]
    public void Numbers_present_in_the_evidence_and_requirement_ids_are_not_reported()
    {
        Assert.Empty(SourceEvidence.NumbersMissingFromEvidence("FR-012 requires approval within 3 working days", ["within 3 working days"]));
        Assert.Empty(SourceEvidence.NumbersMissingFromEvidence("99.9% availability", ["The portal shall be available 99.9% of the time"]));
        Assert.Empty(SourceEvidence.NumbersMissingFromEvidence(null, ["anything"]));
        Assert.Equal(new[] { "7" }, SourceEvidence.NumbersMissingFromEvidence("Escalate after 7", []));
    }

    // ---- origin stays outside the checker ------------------------------------------------------

    [Fact]
    public void Checker_neither_takes_nor_returns_an_origin()
    {
        Assert.DoesNotContain(typeof(QuoteCheck).GetProperties(), p => p.Name.Contains("Origin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(EvidenceAssessment).GetProperties(), p => p.Name.Contains("Origin", StringComparison.OrdinalIgnoreCase));
        var parameters = typeof(SourceEvidenceIndex).GetMethods()
            .Concat(typeof(SourceEvidence).GetMethods())
            .SelectMany(m => m.GetParameters());
        Assert.DoesNotContain(parameters, p => p.Name!.Contains("origin", StringComparison.OrdinalIgnoreCase));
    }

    // ---- normalization, constants, determinism -------------------------------------------------

    [Fact]
    public void Normalization_keeps_words_numbers_ids_and_symbols_and_drops_formatting()
    {
        Assert.Equal("fr-012 email the 1,000 users now", SourceEvidence.Normalize("FR-012: “E-mail” the 1,000 users — now!"));
        Assert.Equal("respond in < 1.5 s for 99.9 % of 5-10 users", SourceEvidence.Normalize("Respond in < 1.5 s for 99.9% of 5–10 users."));
        Assert.Equal("must not", SourceEvidence.Normalize("MUST NOT"));
    }

    [Fact]
    public void Thresholds_are_the_documented_constants()
    {
        Assert.Equal(4, SourceEvidence.MinQuoteWords);
        Assert.Equal(2, SourceEvidence.MinFragmentWords);
        Assert.Equal(0.70, SourceEvidence.AlteredOverlapThreshold);
        Assert.Equal(600, SourceEvidence.MaxEllipsisGapChars);
    }

    [Fact]
    public void Checking_is_deterministic_and_tolerates_malformed_unicode()
    {
        var again = SourceEvidenceIndex.Build(Srs);
        foreach (var quote in new[] { "The system shall log the event.", "Managers can export payroll reports to Excel", "the system" })
        {
            Assert.Equal(Index.Check(quote, "FR-005"), again.Check(quote, "FR-005"));
        }

        var broken = SourceEvidenceIndex.Build("Users shall \uD800 log in to the portal.");
        AssertVerified(broken.Check("users shall log in to the portal"));
        AssertUnverified(broken.Check("\uDC00\uD800 lone surrogates only here"));
    }
}
