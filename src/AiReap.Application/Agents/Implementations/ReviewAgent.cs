using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Agents.Implementations;

// Step 6 — read-only readiness report for the human Reviewer. It does not compute readiness itself:
// it reports the project's lifecycle exit checks (LifecycleEvidenceService, the same checks that
// gate every stage transition) for the current stage and the two gated moves, Design -> Approved and
// Testing -> Completed, plus earlier stages whose evidence no longer holds. The model only writes a
// narrative over those counts. It changes no artifact, approves nothing and never moves the project.
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
    private readonly ILifecycleEvidenceService _evidence;
    private readonly ILogger<ReviewAgent> _logger;

    public ReviewAgent(IAiChatClient chatClient, IAiReapDbContext db, ICurrentUser currentUser, ILifecycleEvidenceService evidence, ILogger<ReviewAgent> logger)
    {
        _chatClient = chatClient;
        _db = db;
        _currentUser = currentUser;
        _evidence = evidence;
        _logger = logger;
    }

    public AgentKind Kind => AgentKind.Review;
    public string DisplayName => "Review Agent";
    public string Responsibility => "Reports lifecycle readiness from the project's exit checks: what still blocks the current stage and the Design → Approved and Testing → Completed gates, and any earlier stage whose evidence no longer holds.";
    public string Inputs => "The project's lifecycle exit checks (artifacts, human reviews and approvals, work records, conflicts, impact notices).";
    public string Outputs => "A readiness report for the human Reviewer. Read-only: creates, approves and advances nothing.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.Reviewer);
    public IReadOnlyList<ProjectStatus> LifecycleStages => [ProjectStatus.Design, ProjectStatus.Testing];
    public string LifecycleContribution => "Readiness support for the Reviewer at the Design → Approved and Testing → Completed gates. The report is not evidence and approves nothing.";

    // The gates this report always covers, besides the project's current stage.
    private static readonly ProjectStatus[] Gates = [ProjectStatus.Design, ProjectStatus.Testing];

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var current = await _db.Projects.Where(p => p.Id == context.ProjectId).Select(p => p.Status).FirstAsync(cancellationToken);
        var stages = await _evidence.GetAsync(context.ProjectId, cancellationToken);
        var currentIndex = ProjectLifecyclePolicy.IndexOf(current);

        string Move(StageEvidence stage) => ProjectLifecyclePolicy.Next(stage.Stage) is { } next
            ? $"{stage.Label} → {ProjectLifecyclePolicy.Stage(next).Label}"
            : stage.Label;
        int Failing(StageEvidence stage) => stage.Checks.Count(c => !c.Passed);

        var reviewed = stages
            .Where(s => (s.Stage == current || Gates.Contains(s.Stage)) && s.Checks.Count > 0)
            .ToList();
        var gaps = stages
            .Where(s => ProjectLifecyclePolicy.IndexOf(s.Stage) < currentIndex && !s.Passed && !Gates.Contains(s.Stage))
            .ToList();

        var findings = new List<string>();
        foreach (var stage in reviewed)
        {
            findings.AddRange(stage.Checks.Where(c => !c.Passed).Select(c => $"{Move(stage)}: {c.Label} — {c.Detail}"));
        }

        findings.AddRange(gaps.Select(g => $"Evidence gap: {g.Label} has {Failing(g)} failing check(s) although the project has moved past it."));

        StageEvidence Of(ProjectStatus status) => stages.Single(s => s.Stage == status);
        var metrics = new Dictionary<string, int>
        {
            ["currentStageChecksFailing"] = Failing(Of(current)),
            ["designGateChecksFailing"] = Failing(Of(ProjectStatus.Design)),
            ["testingGateChecksFailing"] = Failing(Of(ProjectStatus.Testing)),
            ["earlierStagesWithEvidenceGaps"] = stages.Count(s => ProjectLifecyclePolicy.IndexOf(s.Stage) < currentIndex && !s.Passed),
            ["checksPassing"] = reviewed.Sum(s => s.Checks.Count(c => c.Passed)),
            ["checksReviewed"] = reviewed.Sum(s => s.Checks.Count)
        };

        var summary = await NarrateAsync(context, metrics, findings.Count, cancellationToken);

        return new AgentOutput(
            summary,
            metrics,
            Array.Empty<AgentArtifactRef>(),
            findings,
            "This report supports the Reviewer's own decision at the Design → Approved and Testing → Completed gates (QA's sign-off is given when QA submits Testing). "
            + "Approving this step only completes the agent run: it does not approve the project stage, move the project, or approve any artifact, and nothing is deployed.");
    }

    private async Task<string> NarrateAsync(AgentContext context, Dictionary<string, int> metrics, int findingCount, CancellationToken cancellationToken)
    {
        var fallback = findingCount == 0
            ? "Every lifecycle exit check reviewed here passes."
            : $"{findingCount} lifecycle check(s) are not met; see the findings below.";

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
            // shouldn't fail the whole step - but it's still worth knowing about in production.
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
