namespace AiReap.Domain.Enums;

// §5 — how a ProjectStageTransition moved (or proposed to move) Project.Status. Stored as int.
public enum StageTransitionKind
{
    // Starting point of a project's lifecycle history: written when a project is created, and
    // backfilled once for projects that existed before lifecycle tracking. Not a status change.
    Baseline = 0,

    // One stage forward, as the lifecycle declares. Never skips a stage.
    Advance = 1,

    // Administrator-only move to an earlier stage, always with a reason.
    Rollback = 2
}
