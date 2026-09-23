namespace AiReap.Application.Dashboard;

// §28 — project-level metrics.
public record ProjectDashboardResponse(
    int FunctionalRequirementCount,
    int NonFunctionalRequirementCount,
    int BusinessRuleCount,
    int UserStoryCount,
    int AcceptanceCriterionCount,
    int OpenClarificationCount,
    int ApprovedCount,
    int PendingReviewCount,
    int RejectedCount,
    int ConflictCount,
    IReadOnlyList<RecentChangeItem> RecentChanges);

public record RecentChangeItem(string Code, string Title, string ArtifactType, string Status, DateTime UpdatedAt);
