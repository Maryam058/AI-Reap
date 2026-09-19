using System.Text.Json;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Persistence;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Dashboard;

public class DashboardService : IDashboardService
{
    private readonly IAiReapDbContext _db;

    public DashboardService(IAiReapDbContext db)
    {
        _db = db;
    }

    public async Task<ProjectDashboardResponse> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var artifacts = await _db.Artifacts
            .Where(a => a.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        int CountOf(ArtifactType type) => artifacts.Count(a => a.ArtifactType == type);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var openClarifications = artifacts
            .Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion)
            .Count(a =>
            {
                var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(a.DataJson, options);
                return payload?.ClarificationStatus == "Open";
            });

        var recentChanges = artifacts
            .OrderByDescending(a => a.UpdatedAt)
            .Take(10)
            .Select(a => new RecentChangeItem(a.Code, a.Title, a.ArtifactType.ToString(), a.Status.ToString(), a.UpdatedAt))
            .ToList();

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
            RecentChanges: recentChanges);
    }
}
