using AiReap.Application.Lifecycle;
using AiReap.Application.Traceability;
using AiReap.Domain.Enums;

namespace AiReap.Application.LifecycleReview;

// Read-only, historical review of a project's lifecycle: which artifacts were produced in each stage,
// by whom, from what, and how they moved on. A projection over existing data (stage transitions,
// artifacts, versions, reviews, work records, AI executions, agent runs, relationships) - nothing
// here is stored. A stage is attributed from timestamps against the stage windows in
// ProjectStageTransitions; a null stage means it cannot be determined ("Stage attribution unavailable").

public record LifecycleReviewResponse(
    Guid ProjectId,
    string ProjectName,
    ProjectStatus CurrentStatus,
    bool IsCompleted,
    DateTime? CompletedAt,
    // False when the lifecycle history starts with a backfilled baseline (the project predates
    // lifecycle tracking), so events before that baseline have no stage.
    bool HistoryComplete,
    ProjectDefinitionReview Definition,
    LifecycleReviewTotals Totals,
    IReadOnlyList<StageReviewSummary> Stages,
    IReadOnlyList<ReviewedArtifact> Artifacts,
    IReadOnlyList<ReviewedRequirementSource> RequirementSources,
    IReadOnlyList<ReviewTimelineEvent> Timeline,
    IReadOnlyList<TraceabilityRow> Traceability);

// The Draft stage's content lives on the Project row, which keeps no history: these are the
// current values, with when the project was created and last changed.
public record ProjectDefinitionReview(
    string? Description,
    string? BusinessProblem,
    string? Objectives,
    string? Scope,
    string CreatedByUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record LifecycleReviewTotals(
    int TotalArtifacts,
    int AiGenerated,
    int Manual,
    int UnattributedArtifacts,
    IReadOnlyList<ArtifactTypeCount> ByType,
    int Versions,
    int Reviews,
    int AiOperations,
    int AgentRuns,
    int TasksCompleted,
    int TestsPassed,
    int TestsFailed,
    int TestsBlocked,
    int FunctionalRequirements,
    int FullyTracedFunctionalRequirements,
    // Share of functional requirements traced to a story, acceptance criteria, a task and a test case.
    int? TraceabilityCoveragePercent);

public record ArtifactTypeCount(ArtifactType ArtifactType, int Count, int AiGenerated, int Manual);

public record StageVisit(
    DateTime EnteredAt,
    DateTime? LeftAt,
    StageTransitionKind EnteredBy,
    string? EnteredByUserId,
    string? EnteredByRole,
    // How the stage was left: an advance to the next stage or a rollback. Null while still in it.
    StageTransitionKind? LeftBy);

public enum StageReviewState
{
    Completed,
    Current,
    NotReached
}

public record StageReviewSummary(
    ProjectStatus Stage,
    string Label,
    string OwnerRole,
    StageReviewState State,
    IReadOnlyList<StageVisit> Visits,
    DateTime? FirstEnteredAt,
    // When the project last left this stage forwards; for the final stage, when it was reached.
    DateTime? CompletedAt,
    // The project passed this stage but no recorded visit covers it (history starts later).
    bool HistoryUnavailable,
    // Artifacts first created while the project was in this stage.
    IReadOnlyList<Guid> CreatedArtifactIds,
    IReadOnlyList<ArtifactTypeCount> CreatedByType,
    StageActivity Activity,
    // Requests and moves out of (or rolled back from) this stage, with gate decisions and comments.
    IReadOnlyList<StageTransitionResponse> Transitions);

// Everything recorded while the project was in the stage, including work on artifacts created earlier.
public record StageActivity(
    int ArtifactsCreated,
    int AiGenerated,
    int Manual,
    int ArtifactsEdited,
    int VersionsRecorded,
    int Approvals,
    int Rejections,
    int Comments,
    int AiOperations,
    int AgentStepsRun,
    int TasksCompleted,
    int TestsPassed,
    int TestsFailed,
    int TestsBlocked,
    int RequirementSourcesAdded,
    // Artifacts created earlier that were edited, reviewed or had work recorded in this stage.
    IReadOnlyList<Guid> TouchedArtifactIds);

public record ReviewedArtifact(
    Guid Id,
    string Code,
    string Title,
    ArtifactType ArtifactType,
    ArtifactStatus Status,
    ArtifactOrigin Origin,
    int CurrentVersion,
    int? ApprovedVersion,
    int VersionCount,
    string CreatedByUserId,
    DateTime CreatedAt,
    string? UpdatedByUserId,
    DateTime UpdatedAt,
    // The stage the project was in when the artifact was created; null if that can't be determined.
    ProjectStatus? CreatedInStage,
    // The lifecycle stage whose exit checks this artifact type serves (by type, from the lifecycle policy).
    ProjectStatus ServesStage,
    Guid? RequirementSourceId,
    ArtifactAiProvenance? Ai,
    ArtifactReviewSummary? LatestReview,
    int ReviewCount,
    ArtifactWorkSummary? LatestWork,
    IReadOnlyList<ReviewedArtifactLink> Links);

public record ArtifactAiProvenance(
    Guid ExecutionId,
    string OperationType,
    string Model,
    string? PromptTemplateVersion,
    string UserId,
    DateTime Timestamp,
    bool? Accepted,
    // The agent run and step whose output listed this artifact, when it came from the agent pipeline.
    Guid? AgentRunId,
    AgentKind? Agent);

public record ArtifactReviewSummary(string ReviewerUserId, string? ReviewerRole, ReviewDecision Decision, int? VersionNumber, string? Comment, DateTime ReviewedAt);

public record ArtifactWorkSummary(WorkOutcome Outcome, int VersionNumber, string RecordedByUserId, string? RecordedByRole, DateTime RecordedAt, string? Note);

public record ReviewedArtifactLink(Guid RelatedArtifactId, string Code, ArtifactType ArtifactType, RelationshipType RelationshipType, bool IsOutgoing);

// FileName is withheld from callers who may not read raw sources (ArtifactVisibility.CanReadRawSources).
public record ReviewedRequirementSource(Guid Id, RequirementSourceType SourceType, string? FileName, string CreatedByUserId, DateTime CreatedAt, ProjectStatus? CreatedInStage);

public enum TimelineEventKind
{
    ProjectCreated,
    StageTransition,
    RequirementSourceAdded,
    AiGeneration,
    AgentStep,
    ArtifactsCreated,
    ArtifactsEdited,
    ArtifactsReviewed,
    WorkRecorded
}

// One entry of the chronological history. Bursts (one AI call producing ten requirements, a reviewer
// approving a batch) are folded into one event with a count and the artifacts involved.
public record ReviewTimelineEvent(
    DateTime At,
    TimelineEventKind Kind,
    string Title,
    string? Detail,
    string? ActorUserId,
    string? ActorRole,
    ProjectStatus? Stage,
    int Count,
    IReadOnlyList<Guid> ArtifactIds);
