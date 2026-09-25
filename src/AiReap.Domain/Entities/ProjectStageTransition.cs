using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §5 — the audit trail of Project.Status, and the request/decision record for gated stages.
// Project.Status only ever changes together with a Completed row here (see ProjectLifecyclePolicy).
// A direct advance is written Completed with the requester as decider; a gated one is Pending
// until a different user decides it.
public class ProjectStageTransition
{
    // RequestedByUserId of the Baseline rows the lifecycle migration backfilled.
    public const string SystemUserId = "system";

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    // Per-project order of lifecycle events, unique with ProjectId. Two concurrent transitions
    // read the same last value and the second insert fails, so neither can overwrite the other.
    public int Sequence { get; set; }

    public StageTransitionKind Kind { get; set; }
    public StageTransitionState State { get; set; }

    // Null only on a Baseline row.
    public ProjectStatus? FromStatus { get; set; }
    public ProjectStatus ToStatus { get; set; }

    public string RequestedByUserId { get; set; } = string.Empty;
    // The role the requester acted under (users can hold several roles).
    public string? RequestedByRole { get; set; }
    public DateTime RequestedAt { get; set; }
    // Submission note; for a Rollback this is the mandatory reason.
    public string? RequestNote { get; set; }

    public string? DecidedByUserId { get; set; }
    public string? DecidedByRole { get; set; }
    public DateTime? DecidedAt { get; set; }
    // Mandatory when State is Rejected.
    public string? DecisionReason { get; set; }

    // Set when an Administrator advanced past failing evidence checks; OverrideReason is mandatory then.
    public bool IsOverride { get; set; }
    public string? OverrideReason { get; set; }

    // The evidence checks as they stood when the transition was decided (JSON array).
    public string EvidenceSnapshotJson { get; set; } = "[]";

    // Concurrency token: approve / reject / withdraw is a claim only one caller can win.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<ProjectStageReview> Reviews { get; set; } = new();
}
