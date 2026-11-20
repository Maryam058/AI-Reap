using System.Text.Json;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Application.Sdlc;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Dashboard;

public class DashboardService : IDashboardService
{
    private readonly IAiReapDbContext _db;
    private readonly ISdlcProgressService _sdlcProgress;
    private readonly ICurrentUser _currentUser;

    public DashboardService(IAiReapDbContext db, ISdlcProgressService sdlcProgress, ICurrentUser currentUser)
    {
        _db = db;
        _sdlcProgress = sdlcProgress;
        _currentUser = currentUser;
    }

    public async Task<ProjectDashboardResponse> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var artifacts = await _db.Artifacts
            .Where(a => a.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        int CountOf(ArtifactType type) => artifacts.Count(a => a.ArtifactType == type);

        // Only the open questions a person can see and answer are counted (ClarificationVisibility).
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var openQuestions = artifacts
            .Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion
                        && JsonSerializer.Deserialize<ClarificationQuestionPayload>(a.DataJson, options)?.ClarificationStatus == "Open")
            .ToList();
        var openClarifications = (await ClarificationVisibility.ShownAsync(_db, openQuestions, cancellationToken)).Count;

        // Counts are project-wide aggregates; the list names individual artifacts (code, title), so it
        // follows ArtifactVisibility - a Developer never sees an unapproved requirement here.
        // A clarification question without verified source evidence is never named (ClarificationVisibility).
        var recent = artifacts.Where(_currentUser.CanSee).OrderByDescending(a => a.UpdatedAt).ToList();
        var recentShown = await ClarificationVisibility.FilterAsync(_db, recent.Take(10 + recent.Take(50).Count(a => a.ArtifactType == ArtifactType.ClarificationQuestion)).ToList(), cancellationToken);
        var recentChanges = recentShown
            .Take(10)
            .Select(a => new RecentChangeItem(a.Code, a.Title, a.ArtifactType.ToString(), a.Status.ToString(), a.UpdatedAt))
            .ToList();

        // Each row is one duplicate/conflict pair recorded by ConflictDetectionService - never
        // re-run detection here, just report what's already on record (§28 is a passive summary).
        var artifactIds = artifacts.Select(a => a.Id).ToList();
        var conflictCount = await _db.ArtifactRelationships
            .Where(r => (r.RelationshipType == RelationshipType.DuplicateOf || r.RelationshipType == RelationshipType.ConflictsWith)
                        && artifactIds.Contains(r.SourceArtifactId))
            .CountAsync(cancellationToken);

        // §28 test coverage, same definition as the traceability matrix (FRs with >=1 TestedBy test
        // case / all FRs, every status) but project-wide: computed over all artifacts, not the
        // caller's visible set, and returned only as a count.
        var frIds = artifacts.Where(a => a.ArtifactType == ArtifactType.FunctionalRequirement).Select(a => a.Id).ToList();
        var testCaseIds = artifacts.Where(a => a.ArtifactType == ArtifactType.TestCase).Select(a => a.Id).ToList();
        var testedFrCount = await _db.ArtifactRelationships
            .Where(r => r.RelationshipType == RelationshipType.TestedBy
                        && frIds.Contains(r.SourceArtifactId) && testCaseIds.Contains(r.TargetArtifactId))
            .Select(r => r.SourceArtifactId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new ProjectDashboardResponse(
            FunctionalRequirementCount: CountOf(ArtifactType.FunctionalRequirement),
            NonFunctionalRequirementCount: CountOf(ArtifactType.NonFunctionalRequirement),
            BusinessRuleCount: CountOf(ArtifactType.BusinessRule),
            UserStoryCount: CountOf(ArtifactType.UserStory),
            AcceptanceCriterionCount: CountOf(ArtifactType.AcceptanceCriterion),
            OpenClarificationCount: openClarifications,
            ApprovedCount: artifacts.Count(a => a.Status == ArtifactStatus.Approved),
            PendingReviewCount: artifacts.Count(a => a.Status is ArtifactStatus.AiGenerated or ArtifactStatus.Draft or ArtifactStatus.UnderReview),
            RejectedCount: artifacts.Count(a => a.Status == ArtifactStatus.Rejected),
            ConflictCount: conflictCount,
            RecentChanges: recentChanges,
            OpenImpactNoticeCount: await _db.ArtifactImpactNotices.CountAsync(
                n => n.ProjectId == projectId && n.AcknowledgedAt == null, cancellationToken),
            Phases: await _sdlcProgress.GetAsync(projectId, cancellationToken),
            TestedFunctionalRequirementCount: testedFrCount,
            // Unlike PendingReviewCount (every artifact type), only FRs and NFRs.
            RequirementsNeedingReviewCount: artifacts.Count(a =>
                a.ArtifactType is ArtifactType.FunctionalRequirement or ArtifactType.NonFunctionalRequirement
                && a.Status is ArtifactStatus.AiGenerated or ArtifactStatus.Draft or ArtifactStatus.UnderReview));
    }
}
