using AiReap.Application.Sdlc;

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
    IReadOnlyList<RecentChangeItem> RecentChanges,
    // §22 — downstream artifacts still flagged by an unreviewed upstream change.
    int OpenImpactNoticeCount = 0,
    // §28 — evidence-based completion of each SDLC phase (see SdlcProgressService).
    IReadOnlyList<SdlcPhaseProgress>? Phases = null,
    // §28 test coverage numerator: FRs with at least one TestedBy test case (denominator is
    // FunctionalRequirementCount). Project-wide, independent of the caller's artifact visibility.
    int TestedFunctionalRequirementCount = 0,
    // §28 requirements needing review: FRs + NFRs in AiGenerated, Draft or UnderReview.
    int RequirementsNeedingReviewCount = 0);

public record RecentChangeItem(string Code, string Title, string ArtifactType, string Status, DateTime UpdatedAt);
