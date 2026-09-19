using System.Text;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

public class RequirementQualityService : IRequirementQualityService
{
    private const string SystemPrompt = """
        You are a requirements quality reviewer (§14 of an SDLC automation spec). You are
        given a list of requirements, each with a code, title, and JSON detail. For each one
        that has a genuine quality problem, report it: ambiguity, incompleteness,
        inconsistency, non-testability (e.g. "should load quickly" with no measurable target),
        duplication, contradiction, missing dependency, undefined terminology, excessive
        complexity, or an unclear actor/outcome. Skip requirements that are fine - do not
        invent an issue just to have something to say.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "findings": [
            {"artifactCode": "FR-002", "issue": "string", "recommendation": "string"}
          ]
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public RequirementQualityService(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<QualityFinding>> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");

        var candidates = await _db.Artifacts
            .Where(a => a.RequirementSourceId == requirementSourceId &&
                        (a.ArtifactType == ArtifactType.FunctionalRequirement ||
                         a.ArtifactType == ArtifactType.NonFunctionalRequirement ||
                         a.ArtifactType == ArtifactType.BusinessRule))
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return Array.Empty<QualityFinding>();
        }

        var sb = new StringBuilder();
        foreach (var artifact in candidates)
        {
            sb.Append($"{artifact.Code}: {artifact.Title}\n{artifact.DataJson}\n\n");
        }

        var rawResponse = await _chatClient.CompleteAsync(SystemPrompt, sb.ToString(), cancellationToken);
        var parsed = AiJsonParser.Parse<QualityAiResponse>(rawResponse);

        var findings = parsed.Findings
            .Select(f => (Finding: f, Artifact: candidates.FirstOrDefault(a => a.Code == f.ArtifactCode)))
            .Where(x => x.Artifact is not null)
            .Select(x => new QualityFinding(x.Artifact!.Id, x.Artifact.Code, x.Artifact.Title, x.Finding.Issue, x.Finding.Recommendation))
            .ToList();

        _db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            OperationType = "RequirementQualityAnalysis",
            UserId = _currentUser.UserId,
            Timestamp = DateTime.UtcNow,
            Model = _chatClient.ModelName,
            InputReference = source.Id.ToString(),
            OutputJson = rawResponse,
            Accepted = null
        });

        await _db.SaveChangesAsync(cancellationToken);

        return findings;
    }

    private class QualityAiResponse
    {
        public List<QualityFindingItem> Findings { get; set; } = new();
    }

    private class QualityFindingItem
    {
        public string ArtifactCode { get; set; } = string.Empty;
        public string Issue { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
    }
}
