namespace AiReap.Application.Files;

// §6/§26 intake: extracts plain text from an uploaded file so the rest of the pipeline
// (requirement sources, RAG documents) only ever deals with text. Implemented in
// AiReap.Infrastructure (PdfPig for PDF, DocumentFormat.OpenXml for DOCX) per ADR-001 §4.
public interface IDocumentTextExtractor
{
    bool CanExtract(string fileName);

    Task<string> ExtractAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
}
