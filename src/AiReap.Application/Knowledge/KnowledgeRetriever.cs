using AiReap.Application.Ai;
using AiReap.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Knowledge;

public record RetrievedChunk(string DocumentName, int ChunkIndex, string ChunkText, float Score)
{
    // The reference format the models are asked to cite and that artifacts store, e.g. "notes.pdf#3".
    public string Reference => $"{DocumentName}#{ChunkIndex}";
}

// StaleChunkCount: chunks embedded with a different model than the current one (e.g. the old hash
// stub) - invisible to retrieval until re-indexed.
public record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, int StaleChunkCount, bool HasDocuments);

public interface IKnowledgeRetriever
{
    Task<RetrievalResult> RetrieveAsync(Guid projectId, string query, int topK, CancellationToken cancellationToken = default);
}

// §26 — semantic retrieval over one project's document chunks (the vector store is DocumentChunk
// .Embedding in SQL Server; similarity is computed in-process). Shared by the Copilot and the
// requirement generator so both ground their output in the same source material.
public class KnowledgeRetriever : IKnowledgeRetriever
{
    private const float MinSimilarity = 0.15f;
    private const int MaxQueryChars = 4_000;

    private readonly IAiReapDbContext _db;
    private readonly IEmbeddingClient _embeddingClient;

    public KnowledgeRetriever(IAiReapDbContext db, IEmbeddingClient embeddingClient)
    {
        _db = db;
        _embeddingClient = embeddingClient;
    }

    // Loads the project's chunk vectors and scores them in memory (O(n) per query). Adequate at
    // project-document scale; a database-native vector index is the upgrade path if corpora grow.
    public async Task<RetrievalResult> RetrieveAsync(Guid projectId, string query, int topK, CancellationToken cancellationToken = default)
    {
        var chunks = await _db.DocumentChunks
            .Where(c => c.Document!.ProjectId == projectId && c.Embedding != null)
            .Select(c => new { c.ChunkIndex, c.ChunkText, c.Embedding, DocumentName = c.Document!.FileName, c.EmbeddingModel })
            .ToListAsync(cancellationToken);

        if (chunks.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return new RetrievalResult([], 0, chunks.Count > 0);
        }

        var current = chunks.Where(c => c.EmbeddingModel == _embeddingClient.ModelName).ToList();
        var stale = chunks.Count - current.Count;
        if (current.Count == 0)
        {
            return new RetrievalResult([], stale, true);
        }

        var queryVector = await _embeddingClient.EmbedAsync(query.Length <= MaxQueryChars ? query : query[..MaxQueryChars], cancellationToken);

        var scored = current
            .Select(c => new RetrievedChunk(c.DocumentName, c.ChunkIndex, c.ChunkText,
                EmbeddingCodec.CosineSimilarity(queryVector, EmbeddingCodec.Unpack(c.Embedding!))))
            .Where(c => c.Score >= MinSimilarity)
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .ToList();

        return new RetrievalResult(scored, stale, true);
    }

    // The labelled excerpt block given to a model; labels are what it must cite.
    public static string FormatExcerpts(IEnumerable<RetrievedChunk> chunks) =>
        string.Join("\n\n", chunks.Select(c => $"[{c.Reference}]: {c.ChunkText}"));
}
