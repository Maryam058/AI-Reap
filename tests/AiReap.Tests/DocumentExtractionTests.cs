using System.Text;
using AiReap.Api.Controllers;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Files;
using AiReap.Tests.SrsEval;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using Xunit.Abstractions;

namespace AiReap.Tests;

// Phase 0 / T1 — what DocumentTextExtractor (TXT/PDF/DOCX upload) keeps and loses, on synthetic
// documents built in memory (DocxFixtureBuilder / PdfFixtureBuilder). Offline: no AI provider, no SQL
// Server, no network.
//
// Two kinds of test live here:
//   * behaviour the product relies on (e.g. body text kept in order, scanned PDFs refused) - these
//     must keep passing;
//   * CHARACTERIZATION tests, marked [Trait("KnownGap", "G8")] (audit gap G8, extraction), that assert
//     a CURRENT LIMITATION so it is visible and measured. They do not describe intended behaviour.
//     Each says what the assertion must become once the limitation is fixed; the fix is expected to
//     make the test fail, and the test is then flipped as described and the KnownGap trait removed.
[Trait("Category", "Phase0")]
public class DocumentExtractionTests(ITestOutputHelper output)
{
    private static readonly DocumentTextExtractor Extractor = new();

    private static string ClinicSource => GoldenFixture.Source(GoldenFixture.ClinicBooking);

    private static List<string> NonBlankLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();

    private static async Task<string> ExtractAsync(byte[] bytes, string fileName)
    {
        using var stream = new MemoryStream(bytes);
        return await Extractor.ExtractAsync(stream, fileName);
    }

    // The clinic fixture's requirement lines for one ID prefix, as (id, text).
    private static List<(string Id, string Text)> ClinicRequirements(string prefix) =>
        NonBlankLines(ClinicSource)
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => (Id: l[..l.IndexOf(':')], Text: l[(l.IndexOf(':') + 2)..]))
            .ToList();

    // ---- TXT ----------------------------------------------------------------------------------

    [Fact]
    public async Task Txt_extraction_is_exact()
    {
        var text = await ExtractAsync(Encoding.UTF8.GetBytes(ClinicSource), "clinic.txt");

        Assert.Equal(ClinicSource, text);
    }

    // ---- DOCX ---------------------------------------------------------------------------------

    [Fact]
    public async Task Docx_contents_entry_keeps_its_tab_and_page_but_not_its_field_instruction()
    {
        var bytes = DocxFixtureBuilder.ContentsEntry("4.2 Actors and Roles", "9", "Nurses triage patients.");
        var extracted = await Extractor.ExtractDocumentAsync(bytes, "srs.docx");

        Assert.Equal("4.2 Actors and Roles\t9\n4.2 Actors and Roles\nNurses triage patients.", extracted.Text);
        // The text as earlier uploads were extracted, to match an original attached to one of them.
        Assert.Equal("4.2 Actors and Roles PAGEREF _Toc123 \\h 9\n4.2 Actors and Roles\nNurses triage patients.", extracted.LegacyText);
    }

    [Fact]
    public async Task Docx_plain_paragraphs_are_kept_exactly_and_in_order()
    {
        var lines = NonBlankLines(ClinicSource);
        var text = await ExtractAsync(DocxFixtureBuilder.Plain(lines), "clinic.docx");

        Assert.Equal(lines, text.Split('\n').ToList());
        Assert.Equal(GoldenFixture.Expected(GoldenFixture.ClinicBooking).SourceIds.Defined, SourceIdCoverage.Classify(text).Defined);
    }

    [Fact]
    public async Task Docx_table_keeps_every_cell_in_row_order_with_each_id_defined_and_followed_by_its_text()
    {
        var rows = ClinicRequirements("FR-").Take(6).Select(r => (r.Id, r.Text, "High")).ToList();
        var text = await ExtractAsync(DocxFixtureBuilder.Table("Booking requirements", rows), "table.docx");
        var lines = text.Split('\n').ToList();

        foreach (var (id, requirement, priority) in rows)
        {
            var at = lines.IndexOf(id);
            Assert.True(at >= 0, $"{id} not found on its own line");
            Assert.Equal(requirement, lines[at + 1]);
            Assert.Equal(priority, lines[at + 2]);
        }

        Assert.Equal(rows.Select(r => r.Id), SourceIdCoverage.Classify(text).Defined);
    }

    // CHARACTERIZATION (G8): a table row is flattened to one line per cell; nothing in the extracted
    // text marks which cells belonged to one row. Transition when fixed: assert that each row is
    // rendered as one line (e.g. "FR-001 | <text> | High") and remove the KnownGap trait.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_docx_table_row_structure_is_flattened()
    {
        var rows = ClinicRequirements("FR-").Take(3).Select(r => (r.Id, r.Text, "High")).ToList();
        var text = await ExtractAsync(DocxFixtureBuilder.Table("Booking requirements", rows), "table.docx");

        Assert.DoesNotContain(text.Split('\n'), l => rows.Any(r => l.Contains(r.Id) && l.Contains(r.Text)));
    }

    // CHARACTERIZATION (G8): requirement IDs produced by Word automatic numbering ("FR-1:", "FR-2:")
    // live in the numbering definitions, not in the paragraph text, and are lost: the text survives but
    // no requirement ID does, so source-ID coverage reports not_applicable. Transition when fixed:
    // assert that "FR-1" ... "FR-n" are extracted in order and Classify reports them as defined.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_docx_auto_numbered_requirement_ids_are_lost()
    {
        var requirements = ClinicRequirements("FR-").Take(5).Select(r => r.Text).ToList();
        var text = await ExtractAsync(DocxFixtureBuilder.AutoNumbered(requirements, "3. Booking"), "numbered.docx");

        Assert.All(requirements, r => Assert.Contains(r, text));
        Assert.Empty(SrsIdExtractor.Extract(text));
        Assert.Equal(SourceIdCoverage.NotApplicable, SourceIdCoverage.Compute(text, []).Status);
    }

    // CHARACTERIZATION (G8): only the main document body is read; text in page headers, page footers
    // and footnotes is dropped without any warning. Transition when fixed: assert the header, footer
    // and footnote sentences are extracted (or reported as skipped) and remove the KnownGap trait.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_docx_header_footer_and_footnote_text_is_dropped()
    {
        const string body = "FR-001: Receptionists must be able to book a consultation room for a named clinician and a time slot.";
        const string header = "FR-020: Every page of the booking screen shows the clinic name.";
        const string footer = "FR-021: Printed booking lists show the print date.";
        const string footnote = "FR-022: Deposits are refunded within five working days.";
        var text = await ExtractAsync(DocxFixtureBuilder.HeaderFooterFootnote(body, header, footer, footnote), "parts.docx");

        Assert.Contains(body, text);
        Assert.DoesNotContain("FR-020", text);
        Assert.DoesNotContain("FR-021", text);
        Assert.DoesNotContain("FR-022", text);
    }

    // ---- PDF ----------------------------------------------------------------------------------

    private static readonly IReadOnlyList<string> PdfPage1 =
    [
        "3. Booking",
        "FR-001: Receptionists must be able to book a consultation room for a named clinician and a time slot.",
        "FR-002: The system must prevent two bookings of the same room for overlapping time slots.",
        "FR-003: Clinicians must be able to view their own bookings for the coming week.",
    ];

    private static readonly IReadOnlyList<string> PdfPage2 =
    [
        "8. Non-functional requirements",
        "NFR-001: The booking page must load within 2 seconds for 95% of requests.",
        "NFR-005: Staff passwords must be at least 12 characters long and are stored hash-",
        "ed with SHA-256.",
    ];

    // The requirement wording itself (each line after its ID) survives PDF extraction and is found by
    // the quote checker, so SRS quotes taken from a PDF can still be verified.
    [Fact]
    public async Task Pdf_text_keeps_every_requirement_sentence_findable_by_the_quote_checker()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([PdfPage1, PdfPage2]), "clinic.pdf");
        output.WriteLine(text);
        var index = SourceEvidenceIndex.Build(text);

        var sentences = PdfPage1.Concat(PdfPage2.Take(2)).Where(l => l.Contains(": ")).Select(l => l[(l.IndexOf(": ") + 2)..]).ToList();
        Assert.Equal(4, sentences.Count);
        Assert.All(sentences, s => Assert.Equal(EvidenceStatus.Verified, index.Check(s).Status));
        // The word hyphenated across the line break ("hash-" / "ed") is still found as one word.
        Assert.Equal(EvidenceStatus.Verified, index.Check("are stored hashed with SHA-256").Status);
    }

    // Formerly a G8 characterization (PdfPig's page text joined text lines with no separator, gluing
    // "3. BookingFR-001", so IDs after a heading were lost and none was defined). Fixed in Phase 1
    // step 2: every text line is its own line, so every ID is found and each requirement ID is defined.
    [Fact]
    public async Task Pdf_text_keeps_lines_so_ids_after_headings_are_found_and_defined()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([PdfPage1, PdfPage2]), "clinic.pdf");

        Assert.DoesNotContain("BookingFR-001", text);
        Assert.DoesNotContain("requirementsNFR-001", text);
        Assert.Equal(["FR-001", "FR-002", "FR-003", "NFR-001", "NFR-005", "SHA-256"], SrsIdExtractor.Extract(text));
        Assert.Equal(["FR-001", "FR-002", "FR-003", "NFR-001", "NFR-005"], SourceIdCoverage.Classify(text).Defined);
        var coverage = SourceIdCoverage.Compute(text, []);
        Assert.Equal(SourceIdCoverage.Incomplete, coverage.Status);
        Assert.Equal(5, coverage.Uncovered.Count);
    }

    [Fact]
    public async Task Pdf_heading_and_requirement_lines_are_extracted_exactly_one_per_line()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([PdfPage1]), "clinic.pdf");

        Assert.Equal(PdfPage1, NonBlankLines(text));
    }

    [Fact]
    public async Task Pdf_pages_keep_their_order_and_a_page_break_never_joins_two_lines()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([PdfPage1, PdfPage2, ["FR-004: Page three line."]]), "pages.pdf");

        Assert.Equal(PdfPage1.Concat(PdfPage2).Append("FR-004: Page three line."), NonBlankLines(text));
    }

    [Fact]
    public async Task Pdf_lines_ending_or_starting_with_punctuation_are_kept_apart_and_unchanged()
    {
        IReadOnlyList<string> lines =
        [
            "2.1 Scope:",
            "(a) FR-010; bookings are optional?",
            "- FR-011: Rooms (all sites) are listed.",
            "* FR-012 \"quoted\" - 50% <= limit, [draft]!",
            "FR-013:",
            "Text continues on the next line.",
        ];
        var text = await ExtractAsync(PdfFixtureBuilder.Text([lines]), "punctuation.pdf");

        Assert.Equal(lines, NonBlankLines(text));
        Assert.Equal(["FR-010", "FR-011", "FR-012", "FR-013"], SrsIdExtractor.Extract(text));
    }

    // Words drawn as separate pieces on one baseline (as table cells often are) with no space character
    // between them are separated by one space, never glued.
    [Fact]
    public async Task Pdf_pieces_drawn_apart_on_one_baseline_are_separated_by_a_space()
    {
        var text = await ExtractAsync(Pdf([[("FR-001", 50, 790), ("Book a room", 120, 790), ("High", 400, 790)], [("FR-002", 50, 774)]]), "row.pdf");

        Assert.Equal(["FR-001 Book a room High", "FR-002"], NonBlankLines(text));
    }

    // A raised footnote marker (smaller text slightly above the baseline) stays on its line.
    [Fact]
    public async Task Pdf_superscript_stays_on_its_line()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        var end = page.AddText("FR-001: Deposits are refunded.", 11, new PdfPoint(50, 790), font);
        page.AddText("1", 7, new PdfPoint(end.Last().EndBaseLine.X, 794), font);
        page.AddText("FR-002: Next.", 11, new PdfPoint(50, 774), font);

        var text = await ExtractAsync(builder.Build(), "footnote.pdf");

        Assert.Equal(["FR-001: Deposits are refunded.1", "FR-002: Next."], NonBlankLines(text));
    }

    private static readonly IReadOnlyList<string> LeftColumn =
    [
        "FR-001: Receptionists book rooms.",
        "FR-002: Overlapping bookings fail.",
        "FR-003: Clinicians see bookings.",
    ];

    private static readonly IReadOnlyList<string> RightColumn =
    [
        "FR-010: Admins add rooms.",
        "FR-011: Rooms can be retired.",
        "FR-012: Retired rooms are hidden.",
    ];

    // Two columns drawn column by column (the usual content order): each column comes out whole, the
    // left one first, and every requirement line keeps its ID. Reading order comes from the content
    // stream, not from positions.
    [Fact]
    public async Task Pdf_two_columns_drawn_column_by_column_come_out_one_column_after_the_other()
    {
        var left = LeftColumn.Select((l, i) => (l, 50.0, 790.0 - 16 * i));
        var right = RightColumn.Select((l, i) => (l, 320.0, 790.0 - 16 * i));
        var text = await ExtractAsync(Pdf([left.Concat(right).ToList()]), "columns.pdf");

        Assert.Equal(LeftColumn.Concat(RightColumn), NonBlankLines(text));
        Assert.Equal(["FR-001", "FR-002", "FR-003", "FR-010", "FR-011", "FR-012"], SourceIdCoverage.Classify(text).Defined);
    }

    // CHARACTERIZATION (G8): there is no layout analysis (the installed PdfPig build has none), so a
    // content stream that draws two columns row by row - left line, then the right line on the same
    // baseline - is extracted in that order: each row of the two columns becomes one line, and the
    // right column's IDs are only mentioned, not defined. Transition when fixed (layout-aware column
    // detection): assert the columns come out one after the other, as in the column-by-column test,
    // and remove the KnownGap trait.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_pdf_two_columns_drawn_row_by_row_are_merged_line_by_line()
    {
        var rows = LeftColumn.Zip(RightColumn, (l, r) => (l, r)).SelectMany((row, i) => new[] { (row.l, 50.0, 790.0 - 16 * i), (row.r, 320.0, 790.0 - 16 * i) });
        var text = await ExtractAsync(Pdf([rows.ToList()]), "rows.pdf");

        Assert.Equal(LeftColumn.Zip(RightColumn, (l, r) => $"{l} {r}"), NonBlankLines(text));
        Assert.Equal(["FR-001", "FR-002", "FR-003"], SourceIdCoverage.Classify(text).Defined);
    }

    // ---- PDF: line breaks inside an ID and inside wrapped prose (follow-up to line preservation) ----

    // An ID broken after its hyphen at a line end ("NFR-" / "005") is joined back: it is found, stays a
    // mention (it does not start a line), and a quote across the break verifies at the cited ID.
    [Fact]
    public async Task Pdf_requirement_id_split_after_its_hyphen_at_a_line_end_is_rejoined()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([[
            "3. Booking",
            "FR-001: A booking may only be cancelled under the rules of NFR-",
            "005 and must notify the clinician.",
            "FR-003: Clinicians see bookings.",
        ]]), "split.pdf");

        Assert.Equal(
            ["3. Booking", "FR-001: A booking may only be cancelled under the rules of NFR-005 and must notify the clinician.", "FR-003: Clinicians see bookings."],
            NonBlankLines(text));
        Assert.Equal(["FR-001", "NFR-005", "FR-003"], SrsIdExtractor.Extract(text));
        var (defined, mentioned) = SourceIdCoverage.Classify(text);
        Assert.Equal(["FR-001", "FR-003"], defined);
        Assert.Equal(["NFR-005"], mentioned);
        Assert.Equal(EvidenceStatus.Verified, SourceEvidenceIndex.Build(text).Check("cancelled under the rules of NFR-005 and must notify", "FR-001").Status);
    }

    // A line ending in a hyphen is joined to the next only when the two halves make a requirement ID:
    // a hyphenated word, a lower-case prefix, a following line that does not start with a digit, or a
    // complete ID at the line end all keep their line break.
    [Theory]
    [InlineData("FR-001: Codes use the prefix FR-", "Overview of the booking codes.")]
    [InlineData("FR-001: Bookings made pre-", "2020 are archived.")]
    [InlineData("FR-001: Passwords are stored hash-", "ed with SHA-256.")]
    [InlineData("FR-001: Rooms follow FR-002 and", "2 rooms are reserved.")]
    public async Task Pdf_line_ending_in_a_hyphen_is_not_joined_unless_it_splits_an_id(string line, string next)
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([[line, next]]), "hyphen.pdf");

        Assert.Equal([line, next], NonBlankLines(text));
    }

    // A prose line that runs on into an ID mentioned at the start of the next line is joined to it,
    // so the ID is not taken as defined; the requirement before it keeps its whole sentence, and a
    // quote of it verifies at that requirement's ID.
    [Fact]
    public async Task Pdf_id_at_the_start_of_a_wrapped_prose_line_is_mentioned_not_defined()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([[
            "3. Booking",
            "FR-001: A booking can be cancelled only when the clinic rule in",
            "FR-002 is satisfied by the receptionist.",
            "FR-003: Clinicians see bookings.",
        ]]), "wrapped.pdf");

        Assert.Equal(
            ["3. Booking", "FR-001: A booking can be cancelled only when the clinic rule in FR-002 is satisfied by the receptionist.", "FR-003: Clinicians see bookings."],
            NonBlankLines(text));
        var (defined, mentioned) = SourceIdCoverage.Classify(text);
        Assert.Equal(["FR-001", "FR-003"], defined);
        Assert.Equal(["FR-002"], mentioned);
        Assert.Equal(EvidenceStatus.Verified, SourceEvidenceIndex.Build(text).Check("cancelled only when the clinic rule in FR-002 is satisfied by the receptionist", "FR-001").Status);
        Assert.Equal(["FR-001", "FR-003"], SourceIdCoverage.Compute(text, []).Uncovered);
    }

    // An ID that starts a line stays defined (and the lines stay apart) when the line before ends a
    // sentence or clause, is a section heading, or the ID is followed by a definition delimiter, a
    // bullet or nothing - whatever the line before it.
    [Theory]
    [InlineData("3. Booking", "FR-002 Users sign in.")]
    [InlineData("FUNCTIONAL REQUIREMENTS", "FR-002 Users sign in.")]
    [InlineData("Functional requirements:", "FR-002 Users sign in.")]
    [InlineData("FR-001: Users sign in.", "FR-002 Users sign out.")]
    [InlineData("FR-001: Users may sign in;", "FR-002 Users sign out.")]
    [InlineData("FR-001: Users may sign in when allowed by", "FR-002: Users sign out.")]
    [InlineData("FR-001: Users may sign in when allowed by", "FR-002 | Users sign out | High")]
    [InlineData("FR-001: Users may sign in when allowed by", "FR-002) Users sign out.")]
    [InlineData("FR-001: Users may sign in when allowed by", "FR-002 - Users sign out.")]
    [InlineData("FR-001: Users may sign in when allowed by", "- FR-002 Users sign out.")]
    [InlineData("FR-001: Users may sign in when allowed by", "FR-002")]
    public async Task Pdf_id_starting_a_line_after_a_sentence_end_heading_or_with_a_delimiter_stays_defined(string previous, string line)
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([[previous, line]]), "defined.pdf");

        Assert.Equal([previous, line], NonBlankLines(text));
        Assert.Contains("FR-002", SourceIdCoverage.Classify(text).Defined);
    }

    // An ID mentioned inside a line is unaffected: still mentioned only, and nothing is joined.
    [Fact]
    public async Task Pdf_id_mentioned_inside_a_line_stays_mentioned_only()
    {
        IReadOnlyList<string> lines = ["FR-001: Rooms are booked as described in FR-009 and", "the clinic rules."];
        var text = await ExtractAsync(PdfFixtureBuilder.Text([lines]), "mention.pdf");

        Assert.Equal(lines, NonBlankLines(text));
        var (defined, mentioned) = SourceIdCoverage.Classify(text);
        Assert.Equal(["FR-001"], defined);
        Assert.Equal(["FR-009"], mentioned);
    }

    // Wrapped prose is also joined when the mentioned ID is followed by a comma or a full stop.
    [Theory]
    [InlineData("FR-001: A booking follows the rules in", "FR-002, FR-003 and FR-004.",
        "FR-001: A booking follows the rules in FR-002, FR-003 and FR-004.", new[] { "FR-002", "FR-003", "FR-004" })]
    [InlineData("FR-001: Cancellation follows the rule set out in", "FR-002. Refunds are manual.",
        "FR-001: Cancellation follows the rule set out in FR-002. Refunds are manual.", new[] { "FR-002" })]
    public async Task Pdf_id_at_the_start_of_a_wrapped_prose_line_followed_by_a_comma_or_full_stop_is_joined(
        string previous, string line, string joined, string[] mentioned)
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([[previous, line]]), "wrapped.pdf");

        Assert.Equal([joined], NonBlankLines(text));
        var classified = SourceIdCoverage.Classify(text);
        Assert.Equal(["FR-001"], classified.Defined);
        Assert.Equal(mentioned, classified.MentionedOnly);
    }

    // Formerly a G8 characterization (the two lines were joined and FR-002 reported mentioned only).
    // A requirement without a full stop followed by one with no delimiter after its ID: the second
    // ID is followed by a capitalised word, which starts a definition, not running prose.
    [Theory]
    [InlineData("FR-001 Users sign in", "FR-002 Users sign out")]
    [InlineData("FR-001 Users sign in High", "FR-002 Users sign out High")]
    public async Task Pdf_adjacent_requirements_without_delimiters_stay_apart_and_both_are_defined(string first, string second)
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([[first, second]]), "unpunctuated.pdf");

        Assert.Equal([first, second], NonBlankLines(text));
        Assert.Equal(["FR-001", "FR-002"], SourceIdCoverage.Classify(text).Defined);
    }

    // A Title Case heading (not recognised as a section heading: it has lower-case letters and no
    // number) followed by a requirement with no delimiter after its ID stays apart.
    [Fact]
    public async Task Pdf_title_case_heading_followed_by_an_undelimited_requirement_stays_apart()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([["Booking Requirements", "FR-001 Receptionists book rooms"]]), "heading.pdf");

        Assert.Equal(["Booking Requirements", "FR-001 Receptionists book rooms"], NonBlankLines(text));
        Assert.Equal(["FR-001"], SourceIdCoverage.Classify(text).Defined);
    }

    // A running page header (ending on a digit) at the top of each page, followed by a requirement
    // with no delimiter after its ID, stays apart from it on every page.
    [Fact]
    public async Task Pdf_page_header_followed_by_an_undelimited_requirement_stays_apart()
    {
        const string header = "Clinic Booking System SRS v1.2";
        var text = await ExtractAsync(PdfFixtureBuilder.Text([
            [header, "FR-001 Receptionists book rooms High"],
            [header, "FR-002 Clinicians see bookings Medium"],
        ]), "header.pdf");

        Assert.Equal([header, "FR-001 Receptionists book rooms High", header, "FR-002 Clinicians see bookings Medium"], NonBlankLines(text));
        Assert.Equal(["FR-001", "FR-002"], SourceIdCoverage.Classify(text).Defined);
    }

    // A requirement table drawn as cells with no delimiter characters: each row is its own line and
    // every row's ID is defined.
    [Fact]
    public async Task Pdf_three_row_requirement_table_without_delimiters_keeps_every_row_defined()
    {
        var text = await ExtractAsync(Pdf([[
            ("FR-001", 50, 790), ("Book a room", 120, 790), ("High", 400, 790),
            ("FR-002", 50, 774), ("Cancel a booking", 120, 774), ("Medium", 400, 774),
            ("FR-003", 50, 758), ("View bookings", 120, 758), ("Low", 400, 758),
        ]]), "table.pdf");

        Assert.Equal(["FR-001 Book a room High", "FR-002 Cancel a booking Medium", "FR-003 View bookings Low"], NonBlankLines(text));
        Assert.Equal(["FR-001", "FR-002", "FR-003"], SourceIdCoverage.Classify(text).Defined);
    }

    // CHARACTERIZATION (G8): a line ending in a prefix and hyphen ("FR-") followed by a line starting
    // with a year ("2026 ...") is taken as an ID split after its hyphen and joined into "FR-2026",
    // which is then found as a mentioned ID. Transition when fixed: assert the two lines stay apart
    // and only FR-001 is found, and remove the KnownGap trait.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_pdf_prefix_hyphen_followed_by_a_year_is_joined_as_a_split_id()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([["FR-001: Codes were renamed to FR-", "2026 rules apply."]]), "year.pdf");

        Assert.Equal(["FR-001: Codes were renamed to FR-2026 rules apply."], NonBlankLines(text));
        Assert.Equal(["FR-001", "FR-2026"], SrsIdExtractor.Extract(text));
        Assert.Equal(["FR-001"], SourceIdCoverage.Classify(text).Defined);
    }

    // CHARACTERIZATION (G8): the wrapped-prose rule is textual. A requirement without a full stop
    // followed by one whose text after an undelimited ID starts in lower case ("FR-002 users sign
    // out") reads like prose running on into a mentioned ID, so the lines are joined and FR-002 is
    // mentioned only. Transition when fixed (layout-aware list/table detection): assert both lines
    // stay apart and both IDs are defined, and remove the KnownGap trait.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_pdf_undelimited_requirement_starting_in_lower_case_is_joined()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.Text([["FR-001 Users sign in", "FR-002 users sign out"]]), "lowercase.pdf");

        Assert.Equal(["FR-001 Users sign in FR-002 users sign out"], NonBlankLines(text));
        Assert.Equal(["FR-001"], SourceIdCoverage.Classify(text).Defined);
    }

    // A PDF with text pieces drawn at given positions, in the given order; each inner list is one page.
    private static byte[] Pdf(IEnumerable<IReadOnlyList<(string Text, double X, double Y)>> pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var pieces in pages)
        {
            var page = builder.AddPage(595, 842);
            foreach (var (text, x, y) in pieces)
            {
                page.AddText(text, 11, new PdfPoint(x, y), font);
            }
        }

        return builder.Build();
    }

    [Fact]
    public async Task Pdf_scanned_page_extracts_no_text()
    {
        var text = await ExtractAsync(PdfFixtureBuilder.ImageOnly(), "scan.pdf");

        Assert.True(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task Upload_refuses_a_scanned_pdf_with_the_no_extractable_text_message()
    {
        var bytes = PdfFixtureBuilder.ImageOnly();
        // Only the extractor is reached on this path: the refusal happens before anything is saved.
        var controller = new RequirementSourcesController(null!, null!, null!, null!, null!, null!, null!, Extractor, null!);
        using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "scan.pdf");

        var result = await controller.Upload(Guid.NewGuid(), file, CancellationToken.None);

        var refused = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("No extractable text", refused.Value as string);
    }

    // CHARACTERIZATION (G8): an image-only page inside an otherwise text PDF contributes nothing and
    // nothing says so: the extracted text equals that of the text page alone, with no warning.
    // Transition when fixed: assert that the extraction reports page 2 as having no text layer.
    [Fact]
    [Trait("KnownGap", "G8")]
    public async Task Characterization_pdf_image_only_page_is_skipped_without_warning()
    {
        var mixed = await ExtractAsync(PdfFixtureBuilder.TextThenImage(PdfPage1), "mixed.pdf");
        var textOnly = await ExtractAsync(PdfFixtureBuilder.Text([PdfPage1]), "text.pdf");

        Assert.Equal(textOnly.TrimEnd(), mixed.TrimEnd());
    }

    [Fact]
    public void Generated_png_is_a_valid_png_header()
    {
        var png = PdfFixtureBuilder.SolidPng(4, 3, 0x80);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(png, PdfFixtureBuilder.SolidPng(4, 3, 0x80));
    }
}
