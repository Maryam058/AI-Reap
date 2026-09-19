using System.Text;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public class ConflictDetectionService : IConflictDetectionService
{
    private const string SystemPrompt = """
        You are a requirements analyst (§15 of an SDLC automation spec) looking for potential
        duplicates, near-duplicates, contradictions, and overlaps among a project's functional
        requirements. Only report pairs with a genuine, specific relationship - do not pad the
        list. relationshipType must be exactly "DuplicateOf" (they describe the same thing) or
        "ConflictsWith" (they contradict or cannot both hold).

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "findings": [
            {"codeA": "FR-012", "codeB": "FR-037", "relationshipType": "ConflictsWith", "reason": "string"}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ConflictDetectionService(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ConflictFinding>> DetectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var candidates = await _db.Artifacts
            .Where(a => a.ProjectId == projectId && a.ArtifactType == ArtifactType.FunctionalRequirement)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        if (candidates.Count < 2)
        {
            return Array.Empty<ConflictFinding>();
        }

        var sb = new StringBuilder();
        foreach (var artifact in candidates)
        {
            sb.Append($"{artifact.Code}: {artifact.Title}\n{artifact.DataJson}\n\n");
        }

        var rawResponse = await _chatClient.CompleteAsync(SystemPrompt, sb.ToString(), cancellationToken);
        var parsed = AiJsonParser.Parse<ConflictAiResponse>(rawResponse);

        var existingRelationships = await _db.ArtifactRelationships
            .Where(r => (r.RelationshipType == RelationshipType.DuplicateOf || r.RelationshipType == RelationshipType.ConflictsWith) &&
                        candidates.Select(c => c.Id).Contains(r.SourceArtifactId))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var findings = new List<ConflictFinding>();

        foreach (var item in parsed.Findings)
        {
            var a = candidates.FirstOrDefault(c => c.Code == item.CodeA);
            var b = candidates.FirstOrDefault(c => c.Code == item.CodeB);
            if (a is null || b is null || a.Id == b.Id)
            {
                continue;
            }

            var relationshipType = item.RelationshipType == "DuplicateOf"
                ? RelationshipType.DuplicateOf
                : RelationshipType.ConflictsWith;

            var alreadyRecorded = existingRelationships.Any(r =>
                r.RelationshipType == relationshipType &&
                ((r.SourceArtifactId == a.Id && r.TargetArtifactId == b.Id) ||
                 (r.SourceArtifactId == b.Id && r.TargetArtifactId == a.Id)));

            if (!alreadyRecorded)
            {
                _db.ArtifactRelationships.Add(new ArtifactRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceArtifactId = a.Id,
                    TargetArtifactId = b.Id,
                    RelationshipType = relationshipType,
                    CreatedAt = now
                });
            }

            findings.Add(new ConflictFinding(a.Id, a.Code, a.Title, b.Id, b.Code, b.Title, relationshipType, item.Reason));
        }

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OperationType = "ConflictDetection",
            UserId = _currentUser.UserId,
            Timestamp = now,
            Model = _chatClient.ModelName,
            InputReference = projectId.ToString(),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return findings;
    }

    private class ConflictAiResponse
    {
        public List<ConflictFindingItem> Findings { get; set; } = new();
    }

    private class ConflictFindingItem
    {
        public string CodeA { get; set; } = string.Empty;
        public string CodeB { get; set; } = string.Empty;
        public string RelationshipType { get; set; } = "ConflictsWith";
        public string Reason { get; set; } = string.Empty;
    }
}
