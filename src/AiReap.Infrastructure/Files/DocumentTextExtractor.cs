using System.Text;
using AiReap.Application.Files;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace AiReap.Infrastructure.Files;

// IDocumentTextExtractor for TXT/PDF/DOCX (§6/§26, ADR-001 §4). PDF via PdfPig (per-page text,
// no OCR - scanned/image-only PDFs will extract empty text, which is a known limitation, not a
// bug to silently paper over). DOCX via DocumentFormat.OpenXml (paragraph text only - tables,
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
            sb.AppendLine(page.Text);
        }

        return sb.ToString();
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
