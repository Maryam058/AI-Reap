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
// is a known limitation, not a bug to silently paper over). DOCX via DocumentFormat.OpenXml (paragraph text only - tables,
// headers/footers, and embedded objects are out of scope for a first pass).
public class DocumentTextExtractor : IDocumentTextExtractor
{
    private static readonly string[] SupportedExtensions = { ".txt", ".pdf", ".docx" };

    public bool CanExtract(string fileName) =>
        SupportedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

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

    private static string ExtractPdf(Stream stream)
    {
        using var document = PdfDocument.Open(stream);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(PageText(page.Letters));
        }

        return sb.ToString();
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
        var body = wordDocument.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var paragraphs = body.Descendants<Paragraph>().Select(p => p.InnerText);
        return string.Join("\n", paragraphs);
    }
}
