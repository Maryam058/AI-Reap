namespace AiReap.Domain.Entities;

public class DocumentChunk
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }

    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;

    // Serialized embedding vector (see ADR-001 §2): a float[] packed via Buffer.BlockCopy.
    // Null until embedding is generated. Similarity search is done in-app (cosine, brute-force
    // over a project's chunks) rather than via a vector store/index - see ADR-001 §2 for why
    // that's adequate at this corpus scale.
    public byte[]? Embedding { get; set; }

    // Which embedding model produced Embedding, so stub and real vectors (different dimensions)
    // are never compared against each other.
    public string? EmbeddingModel { get; set; }
}
