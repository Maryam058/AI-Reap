namespace AiReap.Application.Knowledge;

// §26 — Documents/Specs/Notes -> Extraction -> Chunking -> Embeddings -> Vector Store.
// "Extraction" here is plain text only (PDF/DOCX parsing is a separate, still-open gap -
// see ADR-001 §4 and ROADMAP Epic 2.4); this service starts from already-extracted text.
public interface IDocumentService
{
    Task<DocumentResponse> UploadAsync(Guid projectId, string fileName, string contentType, string text, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}
