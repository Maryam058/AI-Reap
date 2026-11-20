using AiReap.Application.Ai;
using Xunit;

namespace AiReap.Tests;

// SourceReferenceIndex: a clarification question's reason links to its source only where the
// reference resolves in the source text. Pure - no host, no AI.
public class SourceReferenceTests
{
    private static readonly Guid SourceId = Guid.Parse("6a1b5f0e-6a77-4c1f-9b55-0d7c7e3b2a11");

    internal const string Srs = """
        Triage Assessment Module - Requirements Specification

        Table of Contents
        3.2 User Roles ........ 4
        12.7 Decline Categories ........ 31

        1. Introduction
        This document describes the triage module used in the emergency department.

        3. Access
        3.2 User Roles
        Nurses and doctors use the module. Administrators manage access permissions for every role.
        3.2.1 Supervisors
        Supervisors may override a triage light after review.

        4. Security
        Passwords are stored hashed.

        12.7 Decline Categories
        The eight decline categories are listed in the decline popup.

        Appendix A - Glossary
        Triage light: the colour assigned to a patient.
        """;

    private static SourceReferenceIndex Index(string text = Srs) => SourceReferenceIndex.Build(SourceId, text);

    private static int LineOf(string text, string line) =>
        Array.FindIndex(SourceReferenceIndex.Lines(text), l => l.Trim() == line);

    [Theory]
    [InlineData("4.2 Actors and Roles\t9")] // a Word contents entry as DOCX text is extracted now
    [InlineData("4.2 Actors and RolesPAGEREF bm12 \\h9")] // ... and as it was extracted before
    [InlineData("4.2 Actors and RolesPAGEREF _Toc123 \\h 9")]
    public void A_Word_contents_entry_is_not_taken_for_the_section_it_lists(string entry)
    {
        var text = $"Table of Contents\n{entry}\n4.3 Constraints\t11\n\n4.2 Actors and Roles\nNurses triage patients.\n4.3 Constraints\nRuns on the PMS.";
        var reference = Assert.Single(Index(text).Resolve("Section 4.2 does not name the supervisor role."));

        Assert.Equal("4.2 Actors and Roles", reference.Heading);
        Assert.Equal(4, reference.Line);
        Assert.Equal("Nurses triage patients.", reference.Excerpt);
    }

    [Fact]
    public void A_section_number_in_the_reason_links_to_its_heading_not_the_table_of_contents()
    {
        const string reason = "Section 12.7 is cited but gives no actual mapping.";
        var reference = Assert.Single(Index().Resolve(reason));

        Assert.Equal("Section 12.7", reference.Label);
        Assert.Equal(0, reference.Start);
        Assert.Equal(SourceId, reference.SourceId);
        Assert.Equal("12.7", reference.SectionNumber);
        Assert.Equal("12.7 Decline Categories", reference.Heading);
        Assert.Equal(LineOf(Srs, "12.7 Decline Categories"), reference.Line);
        Assert.Equal($"L{reference.Line}", reference.Anchor);
        Assert.Equal("The eight decline categories are listed in the decline popup.", reference.Excerpt);
        Assert.False(reference.ExcerptIsQuote);
    }

    [Fact]
    public void A_section_runs_through_its_subsections_and_stops_at_the_next_section()
    {
        var reference = Assert.Single(Index().Resolve("See Section 3.2."));
        Assert.Equal(LineOf(Srs, "3.2 User Roles"), reference.Line);
        Assert.True(reference.EndLine > LineOf(Srs, "Supervisors may override a triage light after review."));
        Assert.Equal(LineOf(Srs, "4. Security"), reference.EndLine + 1); // the blank line before it is trimmed off

        var subsection = Assert.Single(Index().Resolve("Subsection 3.2.1 is vague."));
        Assert.Equal("3.2.1", subsection.SectionNumber);
    }

    [Fact]
    public void Several_references_in_one_reason_each_get_their_own_link()
    {
        const string reason = "Sections 3.2 and 12.7 disagree, and §4 says nothing about tokens; see Appendix A.";
        var references = Index().Resolve(reason);

        Assert.Equal(["Sections 3.2", "12.7", "§4", "Appendix A"], references.Select(r => r.Label));
        Assert.Equal(["3.2", "12.7", "4", "A"], references.Select(r => r.SectionNumber));
        foreach (var r in references)
        {
            Assert.Equal(r.Label, reason.Substring(r.Start, r.Label.Length));
        }
    }

    [Fact]
    public void A_section_page_or_heading_that_is_not_in_the_source_is_never_linked()
    {
        Assert.Empty(Index().Resolve("Section 9.9 is missing, as is page 12 and the 'Billing Rules' section."));
        Assert.Empty(Index().Resolve("Not stated in the raw requirement."));
        Assert.Empty(Index("Employees submit leave requests; managers approve.").Resolve("Section 1 is unclear."));
    }

    [Fact]
    public void A_quoted_heading_title_links_to_that_heading()
    {
        var reference = Assert.Single(Index().Resolve("The 'User Roles' section never names the auditor role."));
        Assert.Equal("User Roles", reference.Label);
        Assert.Equal("3.2", reference.SectionNumber);
    }

    [Fact]
    public void A_claimed_reference_is_kept_only_when_it_resolves_in_the_source()
    {
        const string reason = "The SRS describes access permissions in Section 3.2 — User Roles, but not for auditors.";
        var claims = new List<ClaimedSourceReference>
        {
            new() { Label = "Section 3.2 — User Roles", Section = "3.2", Heading = "User Roles", Quote = "Administrators manage access permissions for every role." },
        };

        var reference = Assert.Single(Index().Resolve(reason, claims));
        Assert.Equal("Section 3.2 — User Roles", reference.Label);
        Assert.True(reference.ExcerptIsQuote);
        Assert.Equal("Administrators manage access permissions for every role.", reference.Excerpt);
        Assert.Equal(LineOf(Srs, "Nurses and doctors use the module. Administrators manage access permissions for every role."), reference.Line);
        Assert.Equal("3.2 User Roles", reference.Heading);
    }

    [Fact]
    public void An_invented_or_contradictory_claim_is_dropped()
    {
        const string reason = "Clause 8.1 sets the retention period; Section 4 — Billing is silent; the audit log is unspecified.";
        var claims = new List<ClaimedSourceReference>
        {
            new() { Label = "Clause 8.1", Section = "8.1" },                          // no such section
            new() { Label = "Section 4 — Billing", Section = "4", Heading = "Billing" }, // 4 is Security
            new() { Label = "the audit log", Quote = "Audit entries are kept for ten years." }, // not in the source
            new() { Label = "not in the reason", Section = "3.2" },
        };

        Assert.Empty(Index().Resolve(reason, claims));
    }

    [Fact]
    public void A_claimed_quote_outside_the_cited_section_is_not_used_as_its_evidence()
    {
        const string reason = "Section 3.2 does not say how passwords are kept.";
        var claims = new List<ClaimedSourceReference> { new() { Label = "Section 3.2", Section = "3.2", Quote = "Passwords are stored hashed." } };

        var reference = Assert.Single(Index().Resolve(reason, claims));
        Assert.False(reference.ExcerptIsQuote);
        Assert.Equal(LineOf(Srs, "3.2 User Roles"), reference.Line);
    }

    [Fact]
    public void Markup_in_a_reason_is_never_part_of_a_link()
    {
        const string reason = "<img src=x onerror=alert(1)><script>alert(2)</script> Section 3.2 is unclear.";
        var reference = Assert.Single(Index().Resolve(reason));
        Assert.Equal("Section 3.2", reference.Label);
        Assert.Equal(reason.IndexOf("Section 3.2", StringComparison.Ordinal), reference.Start);
    }

    [Fact]
    public void Line_numbers_follow_the_viewers_line_split_for_any_line_ending()
    {
        var crlf = Srs.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var reference = Assert.Single(Index(crlf).Resolve("Section 12.7"));
        Assert.Equal("12.7 Decline Categories", SourceReferenceIndex.Lines(crlf)[reference.Line]);

        var claims = new List<ClaimedSourceReference> { new() { Label = "popup", Quote = "listed in the decline popup" } };
        var quoted = Assert.Single(Index(crlf).Resolve("The popup is vague.", claims));
        Assert.Contains("listed in the decline popup", SourceReferenceIndex.Lines(crlf)[quoted.Line]);
        Assert.Equal("12.7", quoted.SectionNumber);
    }
}
