using System.IO.Compression;
using System.Text;
using AiReap.Application.Ai;
using AiReap.Application.Files;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AiReap.Infrastructure.Files;

// IDocumentTextExtractor for TXT/PDF/DOCX (§6/§26, ADR-001 §4). PDF via PdfPig (per-page text, one
// line per text line - see PageText; no OCR - scanned/image-only PDFs will extract empty text, which
// is a known limitation, not a bug to silently paper over). DOCX via DocumentFormat.OpenXml: paragraphs
// with their automatic numbering, table rows, notes, headers and footers (DocxText); what it cannot read
// is named in the text, not dropped silently.
public class DocumentTextExtractor : IDocumentTextExtractor
{
    private static readonly string[] SupportedExtensions = { ".txt", ".pdf", ".docx" };

    public bool CanExtract(string fileName) =>
        SupportedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    // Limits on what a DOCX may expand to: it is a ZIP, and a small one can inflate to gigabytes.
    private const long MaxDocxUncompressedBytes = 200L * 1024 * 1024;
    private const int MaxDocxEntries = 5_000;

    public async Task<ExtractedDocument> ExtractDocumentAsync(ReadOnlyMemory<byte> content, string fileName, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var kind = DocumentSignature.Detect(content.Span);
        switch (extension)
        {
            case ".txt":
                if (kind != DocumentSignature.Kind.Unknown)
                {
                    throw new InvalidDocumentException("This file is named .txt but its content is not plain text.");
                }

                using (var stream = new MemoryStream(content.ToArray(), writable: false))
                {
                    return new ExtractedDocument(await ExtractTxtAsync(stream, cancellationToken), null, null);
                }

            case ".pdf":
                if (kind != DocumentSignature.Kind.Pdf)
                {
                    throw new InvalidDocumentException("This file is named .pdf but its content is not a PDF.");
                }

                try
                {
                    using var stream = new MemoryStream(content.ToArray(), writable: false);
                    var text = ExtractPdf(stream, out var pageLineStarts);
                    return new ExtractedDocument(text, OriginalDocumentFormats.Pdf, pageLineStarts);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // PdfPig's own message can quote the file; it is not passed on.
                    throw new InvalidDocumentException("This PDF could not be read. It may be damaged or password-protected.");
                }

            case ".docx":
                if (kind == DocumentSignature.Kind.Ole)
                {
                    throw new InvalidDocumentException("This file is a password-protected or older Word (.doc) document; save it as an unprotected .docx and upload that.");
                }

                if (kind != DocumentSignature.Kind.Zip)
                {
                    throw new InvalidDocumentException("This file is named .docx but its content is not a Word document.");
                }

                CheckDocxPackage(content);
                try
                {
                    using var stream = new MemoryStream(content.ToArray(), writable: false);
                    using var wordDocument = WordprocessingDocument.Open(stream, false, new OpenSettings { MaxCharactersInPart = MaxDocxUncompressedBytes });
                    if (wordDocument.DocumentType != DocumentFormat.OpenXml.WordprocessingDocumentType.Document)
                    {
                        throw new InvalidDocumentException("Macro-enabled Word documents and templates are not accepted; upload a plain .docx.");
                    }

                    return new ExtractedDocument(DocxText(wordDocument), OriginalDocumentFormats.Docx, null,
                        DocxText(wordDocument, legacy: true), ParagraphText(wordDocument));
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or InvalidDocumentException))
                {
                    throw new InvalidDocumentException("This Word document could not be read. It may be damaged.");
                }

            default:
                throw new InvalidDocumentException("Only .txt, .pdf, and .docx uploads are supported.");
        }
    }

    // A Word package is a ZIP with [Content_Types].xml and a main document; its entry count and
    // expanded size are checked before anything is inflated.
    private static void CheckDocxPackage(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content.ToArray(), writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxDocxEntries || zip.Entries.Sum(e => e.Length) > MaxDocxUncompressedBytes)
            {
                throw new InvalidDocumentException("This Word document expands to more than AI-REAP accepts.");
            }

            if (zip.GetEntry("[Content_Types].xml") is null)
            {
                throw new InvalidDocumentException("This file is named .docx but its content is not a Word document.");
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidDocumentException("This Word document could not be read. It may be damaged.");
        }
    }

    public async Task<string> ExtractAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".txt" => await ExtractTxtAsync(stream, cancellationToken),
            ".pdf" => ExtractPdf(stream),
            ".docx" => ExtractDocx(stream),
            _ => throw new NotSupportedException($"Unsupported file type: {extension}")
        };
    }

    private static async Task<string> ExtractTxtAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ExtractPdf(Stream stream) => ExtractPdf(stream, out _);

    // The text, and the line of it each page starts on (lines counted as SourceReferenceIndex.Lines
    // counts them: \r\n, \r or \n ends one). The text is the same either way.
    private static string ExtractPdf(Stream stream, out List<int> pageLineStarts)
    {
        using var document = PdfDocument.Open(stream);
        var sb = new StringBuilder();
        pageLineStarts = new List<int>();
        var line = 0;
        foreach (var page in document.GetPages())
        {
            var pageText = PageText(page.Letters);
            pageLineStarts.Add(line);
            sb.AppendLine(pageText);
            line += LineBreaks(pageText) + 1;
        }

        return sb.ToString();
    }

    private static int LineBreaks(string text)
    {
        var breaks = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                breaks++;
            }
        }

        return breaks;
    }

    // A PDF page's text with its line boundaries kept. PdfPig's page.Text joins every letter with no
    // separator, which glued a heading to the requirement ID on the next line ("3. BookingFR-001").
    // Letters stay in content-stream order - the order page.Text uses, which is the order the producer
    // drew them (for most generated PDFs the reading order, column by column) - and only separators are
    // added: a line break where the next letter is not on the current letter's baseline, a space where
    // it is drawn apart from it on the same baseline with no space character between them. No letter is
    // dropped or reordered by position: there is no layout analysis, so a content stream that
    // interleaves two columns line by line keeps that interleaving.
    // Two line breaks are taken out again (see JoinLines): one inside a requirement ID, and one in
    // prose that runs on into an ID mentioned at the start of the next line.
    private static string PageText(IReadOnlyList<Letter> letters)
    {
        var lines = new List<string>();
        var text = new StringBuilder();
        Letter? previous = null;
        foreach (var letter in letters)
        {
            if (previous is not null)
            {
                if (!OnSameLine(previous, letter))
                {
                    TrimTrailingSpaces(text);
                    lines.Add(text.ToString());
                    text.Clear();
                }
                else if (DrawnApart(previous, letter))
                {
                    text.Append(' ');
                }
            }

            text.Append(letter.Value);
            previous = letter;
        }

        TrimTrailingSpaces(text);
        lines.Add(text.ToString());
        return JoinLines(lines);
    }

    // The page's lines joined by line breaks, except where a line runs on into the next:
    //   * an ID broken after its hyphen ("...the rules of NFR-" / "005 and...") is joined with nothing
    //     between, when the two halves make an ID that neither half holds alone;
    //   * prose that ends mid-sentence (on a letter, digit or comma, and not a section heading) followed
    //     by a line that starts with an ID and then a lower-case letter, comma or full stop ("...the
    //     clinic rule in" / "FR-002 is satisfied...") is joined with a space, so a mentioned ID is not
    //     taken as defined (SourceIdCoverage, SourceEvidenceIndex: an ID that starts a line defines
    //     it). An ID followed by a delimiter, a capitalised word ("FR-002 Users sign out", a table row)
    //     or nothing, or after a bullet, still starts its line. The rule is textual: an undelimited
    //     requirement whose text starts in lower case ("FR-002 users ...") is joined too.
    private static string JoinLines(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder(lines[0]);
        var current = lines[0];
        foreach (var next in lines.Skip(1))
        {
            if (current.Length > 0 && ContinuesSplitId(current, next))
            {
                text.Append(next);
                current += next;
            }
            else if (current.Length > 0 && ContinuesIntoMentionedId(current, next))
            {
                text.Append(' ').Append(next);
                current += " " + next;
            }
            else
            {
                text.Append('\n').Append(next);
                current = next;
            }
        }

        return text.ToString();
    }

    private static bool ContinuesSplitId(string line, string next)
    {
        if (!line.EndsWith('-') || next.Length == 0 || !char.IsDigit(next[0]))
        {
            return false;
        }

        var head = line[(line.LastIndexOf(' ') + 1)..];
        var tail = next.Split(' ', 2)[0];
        var inHead = SrsIdExtractor.Extract(head);
        return SrsIdExtractor.Extract(head + tail).Any(id => !inHead.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ContinuesIntoMentionedId(string line, string next)
    {
        var last = line[^1];
        if (!(char.IsLetterOrDigit(last) || last == ',') || SourceChunker.IsSectionHeading(line))
        {
            return false;
        }

        var id = SrsIdExtractor.Extract(next).Where(i => next.StartsWith(i, StringComparison.Ordinal)).MaxBy(i => i.Length);
        if (id is null || next.Length == id.Length)
        {
            return false;
        }

        // What follows the ID, past any spaces: running prose goes on in lower case or with a comma or
        // full stop ("FR-002 is satisfied", "FR-002, FR-003"); a delimiter, a capitalised word (a
        // definition: "FR-002 Users sign out") or nothing means the ID is defined here.
        var after = next[id.Length..].TrimStart(' ');
        return after.Length > 0 && (char.IsLower(after[0]) || after[0] is ',' or '.');
    }

    // Same orientation, and the letter's baseline start is within half a font size of the previous
    // letter's baseline (measured across the writing direction): super/subscripts stay on their line.
    private static bool OnSameLine(Letter previous, Letter letter)
    {
        if (previous.TextOrientation != letter.TextOrientation)
        {
            return false;
        }

        var (dx, dy) = Direction(previous);
        var offsetX = letter.StartBaseLine.X - previous.StartBaseLine.X;
        var offsetY = letter.StartBaseLine.Y - previous.StartBaseLine.Y;
        return Math.Abs(offsetX * dy - offsetY * dx) <= 0.5 * Math.Max(Size(previous), Size(letter));
    }

    // On the same line, more than a fifth of the font size between the previous letter's end and this
    // letter's start along the writing direction (forward or back), and neither is already a space.
    private static bool DrawnApart(Letter previous, Letter letter)
    {
        if (string.IsNullOrWhiteSpace(previous.Value) || string.IsNullOrWhiteSpace(letter.Value))
        {
            return false;
        }

        var (dx, dy) = Direction(previous);
        var gap = (letter.StartBaseLine.X - previous.EndBaseLine.X) * dx + (letter.StartBaseLine.Y - previous.EndBaseLine.Y) * dy;
        return Math.Abs(gap) > 0.2 * Math.Max(Size(previous), Size(letter));
    }

    // The letter's writing direction as a unit vector; left to right for a zero-width letter.
    private static (double X, double Y) Direction(Letter letter)
    {
        var x = letter.EndBaseLine.X - letter.StartBaseLine.X;
        var y = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
        var length = Math.Sqrt(x * x + y * y);
        return length > 1e-6 ? (x / length, y / length) : (1, 0);
    }

    private static double Size(Letter letter) =>
        letter.PointSize > 0 ? letter.PointSize : Math.Max(letter.GlyphRectangle.Height, 1);

    private static void TrimTrailingSpaces(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
        }
    }

    private static string ExtractDocx(Stream stream)
    {
        using var wordDocument = WordprocessingDocument.Open(stream, false);
        return DocxText(wordDocument);
    }

    // The text of a Word document, in document order (DocxStructure):
    //   * a paragraph is one line; its automatic number, when Word would show one, comes first and is
    //     followed by a tab ("3.2\tLeave approval", "FR-1:\tUsers sign in.") - bullets are left out;
    //   * a table row is one line, its cells joined by " | " (a cell's paragraphs joined by spaces, a
    //     table inside a cell flattened into it), so the cells of a row stay together;
    //   * a footnote or endnote reference is "[^1]" / "[^e1]" where it is made;
    //   * after the body, under the heading "Headers, footers and notes (from the Word document):": each
    //     distinct header and footer line once ("[Header] ...", "[Footer] ..."; bare page numbers left
    //     out), each referenced footnote and endnote ("[Footnote 1] ..."), text boxes ("[Text box] ...")
    //     and an "[Extraction note]" naming what is in the document but not in this text (pictures,
    //     charts and drawings; equations; embedded objects and documents).
    // legacy: Paragraph.InnerText of every paragraph, as the earliest uploads were extracted
    // (ExtractedDocument.LegacyText); it also returns field instructions ("Actors and RolesPAGEREF _Toc123
    // \h 9"). ParagraphText: the visible text of every paragraph, as uploads were extracted until the
    // document structure was kept (ExtractedDocument.ParagraphText).
    private static string DocxText(WordprocessingDocument wordDocument, bool legacy = false)
    {
        var body = wordDocument.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        return legacy
            ? string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText))
            : new DocxStructure(wordDocument.MainDocumentPart!).Text(body);
    }

    private static string ParagraphText(WordprocessingDocument wordDocument)
    {
        var body = wordDocument.MainDocumentPart?.Document?.Body;
        return body is null ? string.Empty : string.Join("\n", body.Descendants<Paragraph>().Select(p => DocxStructure.VisibleText(p)));
    }
}
