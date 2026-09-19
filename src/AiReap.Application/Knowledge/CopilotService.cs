using System.Text;
using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Knowledge;

public class CopilotService : ICopilotService
{
    private const int TopKChunks = 5;
    private const float MinSimilarity = 0.15f;

    private const string SystemPrompt = """
        You are the AI-REAP Project Copilot (§25 of an SDLC automation spec): a read-only,
        project-scoped Q&A assistant. You are given two kinds of context: STRUCTURED FACTS
        (already computed by the system - trust these numbers exactly, never recompute or
        contradict them) and DOCUMENT EXCERPTS (retrieved by semantic search over uploaded
        project documents, each labeled with a document name and chunk index). Answer the
        question using only this context. If an excerpt supports part of your answer, cite it.
        If you don't have enough context to answer, say so rather than guessing.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "answer": "string",
          "citedChunkRefs": [{"documentName": "string", "chunkIndex": 0}],
          "relatedArtifactCodes": ["string"]
        }
        """;

    private readonly IAiReapDbContext _db;
    private readonly IAiChatClient _chatClient;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ICurrentUser _currentUser;

    public CopilotService(IAiReapDbContext db, IAiChatClient chatClient, IEmbeddingClient embeddingClient, ICurrentUser currentUser)
    {
        _db = db;
        _chatClient = chatClient;
        _embeddingClient = embeddingClient;
        _currentUser = currentUser;
    }

    public async Task<CopilotAnswerResponse> AskAsync(Guid projectId, string question, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} not found.");

        var factsBlock = await BuildStructuredFactsAsync(projectId, cancellationToken);
        var (chunksBlock, retrievedChunks) = await RetrieveChunksAsync(projectId, question, cancellationToken);

        var userPrompt = $"STRUCTURED FACTS:\n{factsBlock}\n\nDOCUMENT EXCERPTS:\n{chunksBlock}\n\nQUESTION:\n{question}";

        var rawResponse = await _chatClient.CompleteAsync(SystemPrompt, userPrompt, cancellationToken);
        var parsed = AiJsonParser.Parse<CopilotAiResponse>(rawResponse);

        var citations = parsed.CitedChunkRefs
            .Select(r => retrievedChunks.FirstOrDefault(c => c.DocumentName == r.DocumentName && c.ChunkIndex == r.ChunkIndex))
            .Where(c => c is not null)
            .Select(c => new CopilotCitation(c!.DocumentName, c.ChunkIndex, Snippet(c.ChunkText)))
            .ToList();

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OperationType = "ProjectCopilotQuery",
            UserId = _currentUser.UserId,
            Timestamp = DateTime.UtcNow,
            Model = _chatClient.ModelName,
            InputReference = Snippet(question, 200),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return new CopilotAnswerResponse(parsed.Answer, citations, parsed.RelatedArtifactCodes);
    }

    private async Task<string> BuildStructuredFactsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var artifacts = await _db.Artifacts.Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);

        var frIds = artifacts.Where(a => a.ArtifactType == ArtifactType.FunctionalRequirement).Select(a => a.Id).ToHashSet();
        var testedFrIds = await _db.ArtifactRelationships
            .Where(r => r.RelationshipType == RelationshipType.TestedBy && frIds.Contains(r.SourceArtifactId))
            .Select(r => r.SourceArtifactId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var untestedFrs = artifacts.Where(a => frIds.Contains(a.Id) && !testedFrIds.Contains(a.Id)).ToList();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var openClarifications = artifacts
            .Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion)
            .Where(a =>
            {
                using var doc = JsonDocument.Parse(a.DataJson);
                return doc.RootElement.TryGetProperty("clarificationStatus", out var status) &&
                       string.Equals(status.GetString(), "Open", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        var pendingReview = artifacts
            .Where(a => a.Status is ArtifactStatus.AiGenerated or ArtifactStatus.Draft or ArtifactStatus.UnderReview)
            .ToList();

        var recentChanges = artifacts.OrderByDescending(a => a.UpdatedAt).Take(10).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Total artifacts: {artifacts.Count}");
        sb.AppendLine($"Functional requirements with no linked test case ({untestedFrs.Count}): {string.Join(", ", untestedFrs.Select(a => a.Code))}");
        sb.AppendLine($"Open clarification questions ({openClarifications.Count}): {string.Join(", ", openClarifications.Select(a => a.Code + " - " + a.Title))}");
        sb.AppendLine($"Artifacts pending review ({pendingReview.Count}): {string.Join(", ", pendingReview.Select(a => a.Code))}");
        sb.AppendLine($"Most recently changed artifacts: {string.Join(", ", recentChanges.Select(a => $"{a.Code} ({a.UpdatedAt:u})"))}");
        return sb.ToString();
    }

    private async Task<(string Block, List<RetrievedChunk> Chunks)> RetrieveChunksAsync(Guid projectId, string question, CancellationToken cancellationToken)
    {
        var chunks = await _db.DocumentChunks
            .Where(c => c.Document!.ProjectId == projectId && c.Embedding != null)
            .Select(c => new { c.ChunkIndex, c.ChunkText, c.Embedding, DocumentName = c.Document!.FileName, c.EmbeddingModel })
            .ToListAsync(cancellationToken);

        if (chunks.Count == 0)
        {
            return ("(no project documents uploaded yet)", new List<RetrievedChunk>());
        }

        var questionVector = await _embeddingClient.EmbedAsync(question, cancellationToken);

        var scored = chunks
            .Where(c => c.EmbeddingModel == _embeddingClient.ModelName)
            .Select(c => new
            {
                c.ChunkIndex,
                c.ChunkText,
                c.DocumentName,
                Score = EmbeddingCodec.CosineSimilarity(questionVector, EmbeddingCodec.Unpack(c.Embedding!))
            })
            .Where(c => c.Score >= MinSimilarity)
            .OrderByDescending(c => c.Score)
            .Take(TopKChunks)
            .Select(c => new RetrievedChunk(c.DocumentName, c.ChunkIndex, c.ChunkText))
            .ToList();

        if (scored.Count == 0)
        {
            return ("(no document excerpts matched this question closely enough)", scored);
        }

        var sb = new StringBuilder();
        foreach (var chunk in scored)
        {
            sb.AppendLine($"[{chunk.DocumentName}#{chunk.ChunkIndex}]: {chunk.ChunkText}");
            sb.AppendLine();
        }

        return (sb.ToString(), scored);
    }

    private static string Snippet(string text, int maxLength = 240) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private record RetrievedChunk(string DocumentName, int ChunkIndex, string ChunkText);

    private class CopilotAiResponse
    {
        public string Answer { get; set; } = string.Empty;
        public List<CopilotChunkRef> CitedChunkRefs { get; set; } = new();
        public List<string> RelatedArtifactCodes { get; set; } = new();
    }

    private class CopilotChunkRef
    {
        public string DocumentName { get; set; } = string.Empty;
        public int ChunkIndex { get; set; }
    }
}
