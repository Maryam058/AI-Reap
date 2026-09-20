using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;

namespace AiReap.Application.Agents.Implementations;

// Stage 2 — turns the clarified requirement into structured, quality-checked requirements.
// Each sub-step is skipped if its artifacts already exist, so a retry after a mid-stage failure
// continues instead of duplicating.
public class AnalysisAgent : IAgent
{
    private readonly IRequirementGenerationService _requirements;
    private readonly IBusinessRuleService _businessRules;
    private readonly IUserStoryService _stories;
    private readonly IRequirementQualityService _quality;
    private readonly IConflictDetectionService _conflicts;
    private readonly IAiReapDbContext _db;

    public AnalysisAgent(
        IRequirementGenerationService requirements,
        IBusinessRuleService businessRules,
        IUserStoryService stories,
        IRequirementQualityService quality,
        IConflictDetectionService conflicts,
        IAiReapDbContext db)
    {
        _requirements = requirements;
        _businessRules = businessRules;
        _stories = stories;
        _quality = quality;
        _conflicts = conflicts;
        _db = db;
    }

    public AgentKind Kind => AgentKind.Analysis;
    public string DisplayName => "Analysis Agent";
    public string Responsibility => "Generates functional/non-functional requirements, business rules, user stories and acceptance criteria, then checks them for quality problems and conflicts.";
    public string Inputs => "Requirement source and answered clarification questions.";
    public string Outputs => "FR, NFR, BusinessRule, UserStory, AcceptanceCriterion artifacts; quality and conflict findings.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.BusinessAnalyst);

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var sourceId = context.RequirementSourceId;
        var produced = new List<AgentArtifactRef>();
        var findings = new List<string>();

        // Only artifacts created by this call are reported as "produced"; reused ones are counted in metrics.
        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.FunctionalRequirement, cancellationToken))
        {
            var generated = await _requirements.GenerateAsync(sourceId, cancellationToken);
            produced.AddRange(generated.FunctionalRequirements.Select(AgentSupport.ToRef));
            produced.AddRange(generated.NonFunctionalRequirements.Select(AgentSupport.ToRef));
        }

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.BusinessRule, cancellationToken))
        {
            produced.AddRange((await _businessRules.GenerateAsync(sourceId, cancellationToken)).Select(AgentSupport.ToRef));
        }

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.UserStory, cancellationToken))
        {
            produced.AddRange((await _stories.GenerateStoriesAsync(sourceId, cancellationToken)).Select(AgentSupport.ToRef));
        }

        var stories = await AgentSupport.LoadAsync(_db, sourceId, ArtifactType.UserStory, cancellationToken);
        var storiesWithCriteria = await AgentSupport.TargetsOfAsync(
            _db, ArtifactType.AcceptanceCriterion, RelationshipType.DerivedFrom, context.ProjectId, cancellationToken);
        foreach (var story in stories.Where(s => !storiesWithCriteria.Contains(s.Id)))
        {
            var criteria = await _stories.GenerateAcceptanceCriteriaAsync(story.Id, cancellationToken);
            if (criteria is not null)
            {
                produced.AddRange(criteria.Select(AgentSupport.ToRef));
            }
        }

        foreach (var f in await _quality.AnalyzeAsync(sourceId, cancellationToken))
        {
            findings.Add($"Quality — {f.ArtifactCode}: {f.Issue} Recommendation: {f.Recommendation}");
        }

        foreach (var c in await _conflicts.DetectAsync(context.ProjectId, cancellationToken))
        {
            findings.Add($"Conflict — {c.ArtifactACode} vs {c.ArtifactBCode} ({c.RelationshipType}): {c.Reason} [Human resolution required]");
        }

        var metrics = new Dictionary<string, int>();
        foreach (var type in new[]
                 {
                     ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement, ArtifactType.BusinessRule,
                     ArtifactType.UserStory, ArtifactType.AcceptanceCriterion
                 })
        {
            metrics[type.ToString()] = (await AgentSupport.LoadAsync(_db, sourceId, type, cancellationToken)).Count;
        }

        metrics["qualityFindings"] = findings.Count(f => f.StartsWith("Quality"));
        metrics["conflictFindings"] = findings.Count(f => f.StartsWith("Conflict"));

        return new AgentOutput(
            $"Generated {produced.Count} new artifact(s); {findings.Count} finding(s) need attention.",
            metrics,
            produced,
            findings,
            "Review and approve the generated requirements (Requirements workspace). NFR targets are proposed assumptions until you confirm them. Conflicts are never auto-resolved.");
    }
}
