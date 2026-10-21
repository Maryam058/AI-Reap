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
