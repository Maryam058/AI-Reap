using System.Text;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Ai.Pipeline;

public class RequirementQualityService : IRequirementQualityService
{
    private const string PromptTemplateVersion = "RequirementQualityAnalysis-v1";

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
    private readonly IProjectAccessService _projectAccess;
    private readonly ILogger<RequirementQualityService> _logger;

    public RequirementQualityService(
        IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILogger<RequirementQualityService> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _logger = logger;
    }

    public async Task<IReadOnlyList<QualityFinding>> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == requirementSourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"RequirementSource {requirementSourceId} not found.");
        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);

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

        var (rawResponse, parsed) = await GenerationSupport.CallAiAndParseAsync<QualityAiResponse>(
            _chatClient, _logger, "RequirementQualityAnalysis", source.Id.ToString(), SystemPrompt, sb.ToString(), cancellationToken,
            knownArtifactCodes: candidates.Select(a => a.Code));

        var findings = parsed.Findings
            .Select(f => (Finding: f, Artifact: candidates.FirstOrDefault(a => a.Code == f.ArtifactCode)))
            .Where(x => x.Artifact is not null)
            .Select(x => new QualityFinding(x.Artifact!.Id, x.Artifact.Code, x.Artifact.Title, x.Finding.Issue, x.Finding.Recommendation))
            .ToList();

        GenerationSupport.AddExecutions(
            _db, source.ProjectId, "RequirementQualityAnalysis", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, source.Id.ToString(), rawResponse, DateTime.UtcNow, Array.Empty<Guid>());

        await _db.SaveChangesAsync(cancellationToken);

        return findings;
    }

    private class QualityAiResponse : IValidatableAiResponse
    {
        public List<QualityFindingItem> Findings { get; set; } = new();

        public void Validate(AiResponseValidator v) =>
            v.Items(Findings, "findings", (item, path) =>
            {
                v.CodeExists(item.ArtifactCode, $"{path}.artifactCode");
                v.Required(item.Issue, $"{path}.issue");
                v.Required(item.Recommendation, $"{path}.recommendation");
            });
    }

    private class QualityFindingItem
    {
        public string ArtifactCode { get; set; } = string.Empty;
        public string Issue { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
    }
}
