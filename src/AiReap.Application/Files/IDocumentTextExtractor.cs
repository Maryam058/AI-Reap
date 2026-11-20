namespace AiReap.Application.Files;

// §6/§26 intake: extracts plain text from an uploaded file so the rest of the pipeline
// (requirement sources, RAG documents) only ever deals with text. Implemented in
// AiReap.Infrastructure (PdfPig for PDF, DocumentFormat.OpenXml for DOCX) per ADR-001 §4.
public interface IDocumentTextExtractor
{
    bool CanExtract(string fileName);

    Task<string> ExtractAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);

    // The same text as ExtractAsync, after checking that the content really is the type its name says
    // (DocumentSignature, and that the document opens) - InvalidDocumentException when it is not - plus
    // what the source viewer needs to show the original (ExtractedDocument).
    Task<ExtractedDocument> ExtractDocumentAsync(ReadOnlyMemory<byte> content, string fileName, CancellationToken cancellationToken = default);
}
