using AiReap.Domain.Enums;

namespace AiReap.Domain.Common;

public sealed record LifecycleStage(ProjectStatus Status, string Label, string OwnerRole);

// §5 — the single source of truth for who may move Project.Status, and how. The lifecycle is the
// fixed sequence below; the owner of a stage is the role that does its work and signs off leaving it.
//
//   Draft ─> Requirements Gathering ─> Analysis ─> Design ═> Approved ─> Implementation ─> Testing ═> Completed
//   (BA)         (BA)                    (BA)       (Dev)     (Reviewer)    (Dev)            (QA)      (Reviewer)
//
//   ─>  direct: the owner of the stage being left advances it.
//   ═>  gated: the owner submits, a Reviewer approves or rejects; requester and decider must be
//       different users (enforced by the lifecycle service, which knows the user ids).
//
// Administrator is a system role, not an SDLC owner: it may act on any stage only as an override with
// a reason (ProjectLifecycleService), is the only role that may roll a project back to an earlier
// stage, and the only one that may override failing evidence (reasons mandatory for all of these).
// Nothing else — in particular no AI agent — changes Project.Status.
public static class ProjectLifecyclePolicy
{
    public static readonly IReadOnlyList<LifecycleStage> Stages =
    [
        new(ProjectStatus.Draft, "Draft", Roles.BusinessAnalyst),
        new(ProjectStatus.RequirementsGathering, "Requirements Gathering", Roles.BusinessAnalyst),
        new(ProjectStatus.Analysis, "Analysis", Roles.BusinessAnalyst),
        new(ProjectStatus.Design, "Design", Roles.Developer),
        new(ProjectStatus.Approved, "Approved", Roles.Reviewer),
        new(ProjectStatus.Implementation, "Implementation", Roles.Developer),
        new(ProjectStatus.Testing, "Testing", Roles.QA),
        new(ProjectStatus.Completed, "Completed", Roles.Reviewer),
    ];

    public const string GateApproverRole = Roles.Reviewer;

    public static LifecycleStage Stage(ProjectStatus status) =>
        Stages.FirstOrDefault(s => s.Status == status)
        ?? throw new ArgumentOutOfRangeException(nameof(status), status, "Not a lifecycle stage.");

    public static string OwnerRole(ProjectStatus status) => Stage(status).OwnerRole;

    public static int IndexOf(ProjectStatus status) => Stages.ToList().FindIndex(s => s.Status == status);

    public static ProjectStatus? Next(ProjectStatus status)
    {
        var i = IndexOf(status);
        return i >= 0 && i < Stages.Count - 1 ? Stages[i + 1].Status : null;
    }

    public static bool IsFinal(ProjectStatus status) => Next(status) is null;

    // Leaving these stages needs a Reviewer's decision on the owner's request.
    public static bool IsGated(ProjectStatus from) => from is ProjectStatus.Design or ProjectStatus.Testing;

    public static bool IsLegalAdvance(ProjectStatus from, ProjectStatus to) => Next(from) == to;

    public static bool IsLegalRollback(ProjectStatus from, ProjectStatus to)
    {
        var target = IndexOf(to);
        return target >= 0 && target < IndexOf(from);
    }

    // The role the caller acts under when advancing (or submitting) out of `from`, or null if they
    // may not. The stage owner's role is preferred over Administrator so the audit row names it;
    // Administrator here means "only as an override" (ProjectLifecycleService enforces the reason).
    public static string? AdvanceRole(ProjectStatus from, Func<string, bool> isInRole)
    {
        if (IsFinal(from))
        {
            return null;
        }

        var owner = OwnerRole(from);
        return isInRole(owner) ? owner : isInRole(Roles.Administrator) ? Roles.Administrator : null;
    }

    // The role the caller acts under when approving or rejecting a gated request out of `from`.
    public static string? GateDecisionRole(ProjectStatus from, Func<string, bool> isInRole)
    {
        if (!IsGated(from))
        {
            return null;
        }

        return isInRole(GateApproverRole) ? GateApproverRole : isInRole(Roles.Administrator) ? Roles.Administrator : null;
    }

    public static string? RollbackRole(Func<string, bool> isInRole) =>
        isInRole(Roles.Administrator) ? Roles.Administrator : null;

    public static string? OverrideRole(Func<string, bool> isInRole) =>
        isInRole(Roles.Administrator) ? Roles.Administrator : null;
}
