using AiReap.Application.Sdlc;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;

namespace AiReap.Application.Lifecycle;

// ---- Evidence side: what LifecycleEvidenceService already worked out, kept instead of thrown away --

// One exit check as evaluated: the unchanged SdlcCheck (label / pass-fail / detail), its stable key,
// and - when it fails - what blocks it, grouped by reason. Carries every blocking artifact, visible or
// not; ExitCheckGuidanceService applies ArtifactVisibility before anything leaves the server.
public record CheckEvidence(SdlcCheck Check, string Key, IReadOnlyList<CheckBlocker> Blockers);

public record CheckBlocker(string Reason, IReadOnlyList<BlockerArtifact> Items)
{
    public static CheckBlocker Of(string reason) => new(reason, []);
}

// A blocking artifact, with an optional note ("edited since approval") and the artifacts it relates
// to (the other side of a conflict, the tasks an FR waits on). Related codes are named in the note
// only when the caller may see them.
public record BlockerArtifact(Artifact Artifact, string? Note = null, IReadOnlyList<Artifact>? Related = null);

public record StageCheckEvidence(ProjectStatus Stage, string Label, string OwnerRole, IReadOnlyList<CheckEvidence> Checks)
{
    public StageEvidence ToStageEvidence() => new(Stage, Label, OwnerRole, Checks.Select(c => c.Check).ToList());
}

// ---- Response side: what the current viewer is told ----------------------------------------------

// A superset of SdlcCheck's JSON (label, passed, detail) plus guidance for a failing check. Live and
// per viewer: never written to a transition's evidence snapshot, which stays plain SdlcCheck.
public record ExitCheckView(string Label, bool Passed, string Detail, CheckGuidance? Guidance = null);

public record StageEvidenceView(ProjectStatus Stage, string Label, string OwnerRole, IReadOnlyList<ExitCheckView> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);
}

public record CheckGuidance(
    string Key,
    // CheckKinds: what kind of thing meets this check.
    string Kind,
    string HowTo,
    // For an automatic check: the checks it follows from.
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<BlockerGroup> Blockers);

public record BlockerGroup(
    string Reason,
    string Kind,
    string Title,
    string Instruction,
    // Roles whose action counts as evidence for this group (narrower than the endpoint's roles where
    // lifecycle evidence says so, e.g. only a Developer's work record completes a task).
    IReadOnlyList<string> FixRoles,
    // Advisory: the caller holds one of FixRoles. The endpoints still enforce every rule.
    bool CallerCanFix,
    // Extra context for this caller, e.g. why an Administrator's own record would not count.
    string? CallerNote,
    // User ids of project members holding one of FixRoles.
    IReadOnlyList<string> Assignees,
    CheckDestination Destination,
    // Blocking artifacts the caller may see, at most ExitCheckGuidanceService.MaxItems.
    IReadOnlyList<BlockingItem> Items,
    // Every blocking artifact, including ones not listed.
    int TotalCount,
    // Blocking artifacts the caller may not see (ArtifactVisibility): counted, never identified.
    int HiddenCount);

public record BlockingItem(
    Guid ArtifactId, string Code, string Title, ArtifactType ArtifactType, ArtifactStatus Status, Guid? RequirementSourceId, string? Note);

// Where in the app the work is done: an area the web client maps to a route, and an optional
// section anchor on that page.
public record CheckDestination(string Area, string? Section = null);

public static class CheckKinds
{
    public const string Action = "action";
    public const string Approval = "approval";
    public const string SignOff = "signoff";
    public const string Automatic = "automatic";
}

public static class CheckAreas
{
    public const string Context = "context";
    public const string Members = "members";
    public const string Stakeholders = "stakeholders";
    public const string Sources = "sources";
    public const string Analysis = "analysis";
    public const string Validation = "validation";
    public const string Design = "design";
    public const string Development = "development";
    public const string Testing = "testing";
    public const string Traceability = "traceability";
    public const string Lifecycle = "lifecycle";
}

public static class BlockerReasons
{
    public const string Missing = "missing";
    public const string NotRun = "not-run";
    public const string OpenQuestions = "open-questions";
    public const string NoneYet = "none-yet";
    public const string NoApprovedFrs = "no-approved-frs";
    public const string NeedsApproval = "needs-approval";
    public const string Unresolved = "unresolved";
    public const string NoStory = "no-story";
    public const string NoCriteria = "no-criteria";
    public const string NeedsDeveloperReview = "needs-developer-review";
    public const string OpenNotice = "open-notice";
    public const string Reopened = "reopened";
    public const string NoTask = "no-task";
    public const string NotCompleted = "not-completed";
    public const string TaskReopened = "task-reopened";
    public const string WaitingOnTasks = "waiting-on-tasks";
    public const string NoTest = "no-test";
    public const string TestNeedsApproval = "test-needs-approval";
    public const string NoResult = "no-result";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
    public const string NotImplemented = "not-implemented";
    public const string TestsNotPassed = "tests-not-passed";
    public const string TraceGaps = "trace-gaps";
    public const string SignOff = "signoff";

    // G3 — the same codes artifact approval refuses with (RequirementApprovalEvidence).
    public const string EvidenceUnverified = Artifacts.RequirementApprovalEvidence.EvidenceUnverified;
    public const string EvidenceMissing = Artifacts.RequirementApprovalEvidence.EvidenceMissing;
    public const string TargetNotSupported = Artifacts.RequirementApprovalEvidence.TargetNotSupported;
    public const string TargetUnconfirmed = Artifacts.RequirementApprovalEvidence.TargetUnconfirmed;

    // Blocker groups are listed in this order: earlier steps first (create, then approve, then do).
    public static readonly IReadOnlyList<string> Order =
    [
        Missing, NotRun, NoneYet, NoApprovedFrs, NoTask, NoTest, NoStory, NoCriteria, NeedsApproval, TestNeedsApproval,
        OpenQuestions, Unresolved, NeedsDeveloperReview, OpenNotice, Reopened,
        EvidenceUnverified, EvidenceMissing, TargetNotSupported, TargetUnconfirmed, NotImplemented, WaitingOnTasks,
        NotCompleted, TaskReopened, NoResult, Failed, Blocked, TestsNotPassed, TraceGaps, SignOff
    ];
}
