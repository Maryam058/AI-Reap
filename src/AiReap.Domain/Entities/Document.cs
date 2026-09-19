namespace AiReap.Domain.Entities;

// §26 RAG pipeline input. Kept separate from RequirementSource: chunks feed semantic
// retrieval, not requirement derivation, and are re-chunked/re-embedded independently.
public class Document
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string UploadedByUserId { get; set; } = string.Empty;

    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
}
