using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiReap.Tests.SrsEval;

// Phase 0 — synthetic DOCX fixtures built in memory with DocumentFormat.OpenXml (already referenced by
// AiReap.Infrastructure), so no binary file is committed and nothing is downloaded. Each method returns
// the bytes of a complete .docx package. Content is the clinic fixture's invented text.
public static class DocxFixtureBuilder
{
    // Paragraphs only: one paragraph per line.
    public static byte[] Plain(IEnumerable<string> lines) => Build(body =>
    {
        foreach (var line in lines) body.Append(Para(line));
    });

    // Requirements whose IDs exist only as Word automatic list numbering ("FR-1", "FR-2", ...) defined
    // in the numbering part; the paragraph text itself carries no ID - the usual shape of a Word SRS.
    public static byte[] AutoNumbered(IEnumerable<string> requirementTexts, string heading) => Build((body, main) =>
    {
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(
                new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Decimal },
                    new LevelText { Val = "FR-%1:" })
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });

        body.Append(Para(heading));
        foreach (var text in requirementTexts)
        {
            body.Append(new Paragraph(
                new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
                new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
        }
    });

    // A requirements table: a header row, then one row per (ID, requirement, priority).
    public static byte[] Table(string heading, IEnumerable<(string Id, string Text, string Priority)> rows) => Build(body =>
    {
        body.Append(Para(heading));
        var table = new Table(new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single })));
        table.Append(Row("ID", "Requirement", "Priority"));
        foreach (var (id, text, priority) in rows) table.Append(Row(id, text, priority));
        body.Append(table);
        body.Append(Para("End of requirements table."));
    });

    // One requirement sentence in the body, one in a page header, one in a page footer and one in a
    // footnote referenced from the body.
    public static byte[] HeaderFooterFootnote(string bodyText, string headerText, string footerText, string footnoteText) => Build((body, main) =>
    {
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(Para(headerText));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(Para(footerText));

        var footnotes = main.AddNewPart<FootnotesPart>();
        footnotes.Footnotes = new Footnotes(
            new Footnote(Para(string.Empty)) { Type = FootnoteEndnoteValues.Separator, Id = -1 },
            new Footnote(Para(string.Empty)) { Type = FootnoteEndnoteValues.ContinuationSeparator, Id = 0 },
            new Footnote(Para(footnoteText)) { Id = 1 });

        body.Append(new Paragraph(
            new Run(new Text(bodyText) { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FootnoteReference { Id = 1 })));
        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));
    });

    // Headings numbered by Word from their styles (Heading1 "%1.", Heading2 "%1.%2", as a multi-level
    // list attached to the heading styles), each followed by a body paragraph; a lower-case lettered
    // list under the last heading. Items: (level 1 or 2, heading text, body text).
    public static byte[] NumberedHeadings(IEnumerable<(int Level, string Heading, string Body)> items, IEnumerable<string> letteredItems) => Build((body, main) =>
    {
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new LevelText { Val = "%1." }) { LevelIndex = 0 },
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new LevelText { Val = "%1.%2" }) { LevelIndex = 1 })
            { AbstractNumberId = 1 },
            new AbstractNum(
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.LowerLetter }, new LevelText { Val = "%1)" }) { LevelIndex = 0 })
            { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });

        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            HeadingStyle("Heading1", 0),
            HeadingStyle("Heading2", 1));

        foreach (var (level, heading, text) in items)
        {
            body.Append(new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = level == 1 ? "Heading1" : "Heading2" }),
                new Run(new Text(heading) { Space = SpaceProcessingModeValues.Preserve })));
            body.Append(Para(text));
        }

        foreach (var item in letteredItems)
        {
            body.Append(new Paragraph(
                new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 2 })),
                new Run(new Text(item) { Space = SpaceProcessingModeValues.Preserve })));
        }
    });

    private static Style HeadingStyle(string id, int level) =>
        new(new StyleName { Val = id }, new StyleParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = 1 })))
        { Type = StyleValues.Paragraph, StyleId = id };

    // Body paragraphs, then a heading and a table (header row first) - an appendix the body refers to.
    public static byte[] ParagraphsThenTable(IEnumerable<string> paragraphs, string tableHeading, IEnumerable<string[]> rows) => Build(body =>
    {
        foreach (var p in paragraphs) body.Append(Para(p));
        body.Append(Para(tableHeading));
        var table = new Table(new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single })));
        foreach (var row in rows) table.Append(Row(row));
        body.Append(table);
    });

    // Two sections that both use the same header and footer (the footer also has a page number), and
    // a second footer used by the second section only.
    public static byte[] RepeatedHeaderFooter(string headerText, string footerText, string secondFooterText, IEnumerable<string> lines) => Build((body, main) =>
    {
        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(Para(headerText));
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(Para(footerText), Para("Page 3 of 9"));
        var header2 = main.AddNewPart<HeaderPart>();
        header2.Header = new Header(Para(headerText));
        var footer2 = main.AddNewPart<FooterPart>();
        footer2.Footer = new Footer(Para(secondFooterText));

        foreach (var line in lines) body.Append(Para(line));
        body.Append(new Paragraph(new ParagraphProperties(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }))));
        body.Append(Para("Second section."));
        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header2) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer2) }));
    });

    // A paragraph with a picture (an empty drawing) and one with an equation, around a text line.
    public static byte[] WithPictureAndEquation(string text) => Build(body =>
    {
        body.Append(new Paragraph(new Run(new Drawing())));
        body.Append(Para(text));
        body.Append(new Paragraph(new DocumentFormat.OpenXml.Math.OfficeMath(
            new DocumentFormat.OpenXml.Math.Run(new DocumentFormat.OpenXml.Math.Text("E = mc2")))));
    });

    // A Word table-of-contents entry (title, tab, PAGEREF field showing the page number), then the
    // heading it lists and a line of body text.
    public static byte[] ContentsEntry(string title, string page, string bodyText) => Build(body =>
    {
        body.Append(new Paragraph(
            new Run(new Text(title)),
            new Run(new TabChar()),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGEREF _Toc123 \\h ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(new Text(page)),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        body.Append(Para(title));
        body.Append(Para(bodyText));
    });

    private static TableRow Row(params string[] cells) =>
        new(cells.Select(c => new TableCell(Para(c))));

    private static Paragraph Para(string text) =>
        new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static byte[] Build(Action<Body> fill) => Build((body, _) => fill(body));

    private static byte[] Build(Action<Body, MainDocumentPart> fill)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            fill(body, main);
            main.Document.Save();
        }

        return stream.ToArray();
    }
}
