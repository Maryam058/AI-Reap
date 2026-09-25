using AiReap.Application.Sdlc;
using AiReap.Domain.Enums;

namespace AiReap.Application.Lifecycle;

// The exit checks of one lifecycle stage: the stage may be left only when every check passes
// (or an Administrator overrides, with a reason).
public record StageEvidence(ProjectStatus Stage, string Label, string OwnerRole, IReadOnlyList<SdlcCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);
}

public record AdvanceStageRequest(ProjectStatus TargetStatus, string? Note = null, string? OverrideReason = null);

public record DecideStageTransitionRequest(bool Approve, string? Reason = null, string? OverrideReason = null);

public record RollbackStageRequest(ProjectStatus TargetStatus, string Reason);

public record StageReviewResponse(Guid Id, string ReviewerUserId, string? ReviewerRole, ReviewDecision Decision, string? Comment, DateTime ReviewedAt);

public record StageTransitionResponse(
    Guid Id,
    Guid ProjectId,
    int Sequence,
    StageTransitionKind Kind,
    StageTransitionState State,
    ProjectStatus? FromStatus,
    ProjectStatus ToStatus,
    string RequestedByUserId,
    string? RequestedByRole,
    DateTime RequestedAt,
    string? RequestNote,
    string? DecidedByUserId,
    string? DecidedByRole,
    DateTime? DecidedAt,
    string? DecisionReason,
    bool IsOverride,
    string? OverrideReason,
    IReadOnlyList<SdlcCheck> Evidence,
    IReadOnlyList<StageReviewResponse> Reviews);

// What the caller may do right now; computed from ProjectLifecyclePolicy, the pending request and
// who submitted it, so a UI never has to re-implement the rules.
public record LifecycleActions(
    bool CanAdvance,
    bool CanSubmitForApproval,
    bool CanDecide,
    bool CanWithdraw,
    bool CanRollback,
    bool CanOverride);

public record ProjectLifecycleState(
    Guid ProjectId,
    ProjectStatus Status,
    string StageLabel,
    string OwnerRole,
    ProjectStatus? NextStatus,
    bool NextIsGated,
    // QA's sign-off note, given when submitting Testing for approval, is part of that gate.
    bool RequiresSignOff,
    IReadOnlyList<SdlcCheck> ExitChecks,
    bool ReadyToAdvance,
    StageTransitionResponse? PendingTransition,
    // Stages the project has already passed whose checks no longer hold (e.g. a project that
    // predates lifecycle tracking, or approved work reopened since). Reported, never auto-corrected.
    IReadOnlyList<StageEvidence> EvidenceGaps,
    LifecycleActions Actions,
    // Every lifecycle event, oldest first (baseline, advances, requests and their outcomes, rollbacks).
    IReadOnlyList<StageTransitionResponse> History);

public record StageCommentRequest(string? Comment);

public record WithdrawStageTransitionRequest(string? Note);
