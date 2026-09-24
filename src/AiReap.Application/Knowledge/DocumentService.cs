using AiReap.Application.Ai;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Knowledge;

public class DocumentService : IDocumentService
{
    // Fixed-size sliding window over plain text - simple and predictable, adequate for the
    // spec/notes-sized documents this platform ingests. Revisit (sentence/paragraph-aware
    // chunking) only if real usage shows this splitting mid-thought too often.
    private const int ChunkSize = 1200;
    private const int ChunkOverlap = 200;

    private readonly IAiReapDbContext _db;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ICurrentUser _currentUser;

    public DocumentService(IAiReapDbContext db, IEmbeddingClient embeddingClient, ICurrentUser currentUser)
    {
        _db = db;
        _embeddingClient = embeddingClient;
        _currentUser = currentUser;
    }

    public async Task<DocumentResponse> UploadAsync(Guid projectId, string fileName, string contentType, string text, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} not found.");

        var now = DateTime.UtcNow;
        var document = new Document
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            FileName = fileName,
            ContentType = contentType,
            UploadedAt = now,
            UploadedByUserId = _currentUser.UserId
        };
        _db.Documents.Add(document);

        var chunks = Chunk(text);
        for (var i = 0; i < chunks.Count; i++)
        {
            var vector = await _embeddingClient.EmbedAsync(chunks[i], cancellationToken);
            _db.DocumentChunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                ChunkIndex = i,
                ChunkText = chunks[i],
                Embedding = EmbeddingCodec.Pack(vector),
                EmbeddingModel = _embeddingClient.ModelName
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new DocumentResponse(document.Id, document.ProjectId, document.FileName, document.ContentType, document.UploadedAt, document.UploadedByUserId, chunks.Count);
    }

    public async Task<IReadOnlyList<DocumentResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _db.Documents
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new DocumentResponse(d.Id, d.ProjectId, d.FileName, d.ContentType, d.UploadedAt, d.UploadedByUserId, d.Chunks.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> ReindexAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var model = _embeddingClient.ModelName;
        var stale = await _db.DocumentChunks
            .Where(c => c.Document!.ProjectId == projectId && (c.EmbeddingModel != model || c.Embedding == null))
            .ToListAsync(cancellationToken);

        foreach (var chunk in stale)
        {
            chunk.Embedding = EmbeddingCodec.Pack(await _embeddingClient.EmbedAsync(chunk.ChunkText, cancellationToken));
            chunk.EmbeddingModel = model;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    private static List<string> Chunk(string text)
    {
        var normalized = text.Trim();
        var result = new List<string>();
        if (normalized.Length == 0)
        {
            return result;
        }

        var start = 0;
        while (start < normalized.Length)
        {
            var length = Math.Min(ChunkSize, normalized.Length - start);
            result.Add(normalized.Substring(start, length));

            if (start + length >= normalized.Length)
            {
                break;
            }

            start += ChunkSize - ChunkOverlap;
        }

        return result;
    }
}
