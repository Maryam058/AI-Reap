using AiReap.Domain.Enums;

namespace AiReap.Application.Agents;

// Terminology: an agent run is a pipeline of six agent *steps*, each ending at a human decision.
// A step is not a project lifecycle *stage* (Project.Status); each agent only reports which
// lifecycle stage(s) its output serves. For compatibility the persisted names (AgentStageRun,
// AgentStageStatus) and the JSON field AgentRunResponse.stages are unchanged.

public record AgentDefinitionResponse(
    AgentKind Agent,
    int Order,
    string DisplayName,
    string Responsibility,
    string Inputs,
    string Outputs,
    IReadOnlyList<string> ApproverRoles,
    // The lifecycle stage(s) this agent's output serves, and how. Informational only.
    IReadOnlyList<ProjectStatus> LifecycleStages,
    string LifecycleContribution);

public record AgentStepResponse(
    Guid Id,
    AgentKind Agent,
    int Order,
    string DisplayName,
    AgentStageStatus Status,
    AgentOutput? Output,
    string? Error,
    IReadOnlyList<string> ApproverRoles,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? DecidedByUserId,
    DateTime? DecidedAt,
    string? DecisionComment,
    IReadOnlyList<ProjectStatus> LifecycleStages);

public record AgentRunResponse(
    Guid Id,
    Guid ProjectId,
    Guid RequirementSourceId,
    AgentRunStatus Status,
    string StartedByUserId,
    DateTime StartedAt,
    DateTime? CompletedAt,
    // The run's agent steps. Serialized as "stages" for existing clients; they are pipeline steps.
    IReadOnlyList<AgentStepResponse> Stages,
    // The project's lifecycle stage when the run started (null for runs started before it was recorded).
    ProjectStatus? ProjectStatusAtStart);

public record StartAgentRunRequest(Guid RequirementSourceId);

public record AgentDecisionRequest(bool Approve, string? Comment);

// Read-only overview of AI activity on a project. Agent runs are activity and proposals, never
// lifecycle state: nothing here reads or changes Project.Status except to report it.
public record AgentRunStepSummary(
    AgentKind Agent,
    string DisplayName,
    IReadOnlyList<ProjectStatus> LifecycleStages,
    AgentStageStatus Status,
    IReadOnlyList<string> ApproverRoles,
    string? DecidedByUserId,
    DateTime? DecidedAt,
    // The step serves a later lifecycle stage than the project was in when the run started.
    bool AheadOfProjectStage);

public record AgentRunSummaryItem(
    Guid RunId,
    Guid RequirementSourceId,
    AgentRunStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    ProjectStatus? ProjectStatusAtStart,
    int StepsApproved,
    int StepsRejected,
    int StepCount,
    // The step waiting for a decision, running or failed; null once the run has ended.
    AgentRunStepSummary? CurrentStep,
    // A human decision is due: a step awaiting approval, or a failed step to retry or abandon.
    bool AwaitingHumanDecision,
    IReadOnlyList<AgentRunStepSummary> Steps);

// Agent activity grouped by the lifecycle stage it serves, across all of the project's runs.
public record AgentStageActivity(
    ProjectStatus Stage,
    string Label,
    IReadOnlyList<string> Agents,
    int StepsAwaitingDecision,
    int StepsApproved,
    int StepsRejected,
    int StepsFailed);

public record AgentRunSummaryResponse(
    Guid ProjectId,
    ProjectStatus CurrentProjectStatus,
    int TotalRuns,
    int Active,
    int AwaitingDecision,
    int Failed,
    int Completed,
    int Rejected,
    IReadOnlyList<AgentStageActivity> ByLifecycleStage,
    IReadOnlyList<AgentRunSummaryItem> RecentRuns);
