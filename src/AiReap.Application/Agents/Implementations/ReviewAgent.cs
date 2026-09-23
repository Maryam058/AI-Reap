using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Agents.Implementations;

// Stage 6 — read-only readiness review of everything the earlier agents produced. The gaps are
// computed deterministically from the artifact graph (so they can be trusted); the model only
// writes the narrative on top. It changes no artifact and approves nothing.
public class ReviewAgent : IAgent
{
    private const string PromptTemplateVersion = "AgentReviewSummary-v1";

    private const string SystemPrompt = """
        You are a delivery reviewer at the end of an automated SDLC analysis pipeline. You are
        given counts describing how complete and how reviewed the generated project artifacts are.
        Write a short, plain readiness assessment for the team. Do not invent facts beyond the
        numbers given.

        Respond with ONLY a single JSON object, no markdown fences, no commentary:
        {
          "readinessSummary": "2-4 sentences",
          "recommendation": "ready | not_ready"
        }
        """;

    private readonly IAiChatClient _chatClient;
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ReviewAgent> _logger;

    public ReviewAgent(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, ILogger<ReviewAgent> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    public AgentKind Kind => AgentKind.Review;
    public string DisplayName => "Review Agent";
    public string Responsibility => "Checks the whole artifact set for open questions, unreviewed AI output, requirements without tasks or tests, stories without acceptance criteria, and unresolved conflicts.";
    public string Inputs => "All artifacts and relationships for the requirement source.";
    public string Outputs => "A readiness report. Read-only: creates and approves no artifacts.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.Reviewer);

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var artifacts = await _db.Artifacts
            .Where(a => a.RequirementSourceId == context.RequirementSourceId)
            .ToListAsync(cancellationToken);

        var frs = artifacts.Where(a => a.ArtifactType == ArtifactType.FunctionalRequirement).ToList();
        var stories = artifacts.Where(a => a.ArtifactType == ArtifactType.UserStory).ToList();
        var questions = artifacts.Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion).ToList();

        var frsWithTasks = await AgentSupport.TargetsOfAsync(_db, ArtifactType.ImplementationTask, RelationshipType.Implements, context.ProjectId, cancellationToken);
        var frsWithTests = await AgentSupport.SourcesOfAsync(_db, ArtifactType.TestCase, RelationshipType.TestedBy, context.ProjectId, cancellationToken);
        var storiesWithCriteria = await AgentSupport.TargetsOfAsync(_db, ArtifactType.AcceptanceCriterion, RelationshipType.DerivedFrom, context.ProjectId, cancellationToken);

        var ids = artifacts.Select(a => a.Id).ToList();
        var openConflicts = await _db.ArtifactRelationships.CountAsync(r =>
            (r.RelationshipType == RelationshipType.ConflictsWith || r.RelationshipType == RelationshipType.DuplicateOf)
            && ids.Contains(r.SourceArtifactId), cancellationToken);

        var untestedFrs = frs.Where(f => !frsWithTests.Contains(f.Id)).ToList();
        var unplannedFrs = frs.Where(f => !frsWithTasks.Contains(f.Id)).ToList();
        var storiesMissingCriteria = stories.Where(s => !storiesWithCriteria.Contains(s.Id)).ToList();
        var openQuestions = AgentSupport.OpenClarifications(questions);
        var awaitingReview = artifacts.Count(a => a.ArtifactType != ArtifactType.ClarificationQuestion && a.Status == ArtifactStatus.AiGenerated);
        var rejected = artifacts.Count(a => a.Status == ArtifactStatus.Rejected);

        var findings = new List<string>();
        if (openQuestions > 0) findings.Add($"{openQuestions} clarification question(s) are still open.");
        findings.AddRange(unplannedFrs.Select(f => $"{f.Code} has no implementation task."));
        findings.AddRange(untestedFrs.Select(f => $"{f.Code} has no test case."));
        findings.AddRange(storiesMissingCriteria.Select(s => $"{s.Code} has no acceptance criteria."));
        if (openConflicts > 0) findings.Add($"{openConflicts} conflict/duplicate link(s) still need human resolution.");
        if (awaitingReview > 0) findings.Add($"{awaitingReview} AI-generated artifact(s) have not yet been approved or rejected by a human.");
        if (rejected > 0) findings.Add($"{rejected} artifact(s) were rejected and may need regeneration or rework.");

        var metrics = new Dictionary<string, int>
        {
            ["artifacts"] = artifacts.Count,
            ["awaitingHumanReview"] = awaitingReview,
            ["rejected"] = rejected,
            ["openQuestions"] = openQuestions,
            ["requirementsWithoutTasks"] = unplannedFrs.Count,
            ["requirementsWithoutTests"] = untestedFrs.Count,
            ["storiesWithoutCriteria"] = storiesMissingCriteria.Count,
            ["openConflicts"] = openConflicts
        };

        var summary = await NarrateAsync(context, metrics, findings.Count, cancellationToken);

        return new AgentOutput(
            summary,
            metrics,
            Array.Empty<AgentArtifactRef>(),
            findings,
            findings.Count == 0
                ? "No structural gaps found. Approving completes the run; individual artifacts still need their own human approval."
                : "Resolve the findings (or accept them knowingly) before approving. Approving completes the run — it does not approve individual artifacts and nothing is deployed.");
    }

    private async Task<string> NarrateAsync(AgentContext context, Dictionary<string, int> metrics, int findingCount, CancellationToken cancellationToken)
    {
        var fallback = findingCount == 0
            ? "The artifact set is structurally complete: every requirement has tasks and tests."
            : $"{findingCount} gap(s) were found in the artifact set; see the findings below.";

        _logger.LogInformation(
            "AI call starting: {OperationType} model={Model} input={InputReference}",
            "AgentReviewSummary", _chatClient.ModelName, context.RequirementSourceId);

        string rawResponse;
        try
        {
            rawResponse = await _chatClient.CompleteAsync(SystemPrompt, JsonSerializer.Serialize(metrics), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AI call failed: {OperationType} model={Model} input={InputReference}",
                "AgentReviewSummary", _chatClient.ModelName, context.RequirementSourceId);
            throw;
        }

        _logger.LogInformation(
            "AI call succeeded: {OperationType} input={InputReference} responseLength={ResponseLength}",
            "AgentReviewSummary", context.RequirementSourceId, rawResponse.Length);

        GenerationSupport.AddExecutions(
            _db, context.ProjectId, "AgentReviewSummary", PromptTemplateVersion, _currentUser.UserId,
            _chatClient.ModelName, context.RequirementSourceId.ToString(), rawResponse, DateTime.UtcNow, Array.Empty<Guid>());
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var parsed = AiJsonParser.Parse<ReviewAiResponse>(rawResponse);
            return string.IsNullOrWhiteSpace(parsed.ReadinessSummary) ? fallback : parsed.ReadinessSummary;
        }
        catch (AiOutputValidationException ex)
        {
            // The gap list is deterministic and already authoritative; a malformed narrative
            // shouldn't fail the whole stage - but it's still worth knowing about in production.
            _logger.LogWarning(ex,
                "AI response failed JSON validation, falling back to a deterministic narrative: {OperationType} input={InputReference}",
                "AgentReviewSummary", context.RequirementSourceId);
            return fallback;
        }
    }

    private class ReviewAiResponse
    {
        public string ReadinessSummary { get; set; } = string.Empty;
        public string? Recommendation { get; set; }
    }
}
