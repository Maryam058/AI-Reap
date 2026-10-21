using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Application.Sdlc;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Lifecycle;

// §5/§28 — the exit checks of each lifecycle stage: what must be true before the project may leave
// it. Checks read the project's real data only (artifacts, human reviews, relationships, AI
// executions, work records, members). Agent runs are never read: an agent's output is a proposal
// that counts once a person has approved / reviewed / recorded work on it, exactly as if it had been
// produced by hand. Rejected artifacts are a human "no" and are left out of every check.
public class LifecycleEvidenceService : ILifecycleEvidenceService
{
    // AIExecution.OperationType values written by the pipeline services (GenerationSupport.AddExecutions).
    private const string AnalysisOperation = "RequirementAnalysis";
    private const string QualityOperation = "RequirementQualityAnalysis";
    private const string ConflictOperation = "ConflictDetection";
    private static readonly string[] TrackedOperations = [AnalysisOperation, QualityOperation, ConflictOperation];

    private static readonly ArtifactType[] DesignTypes = [ArtifactType.DesignArtifact, ArtifactType.ApiSpecification, ArtifactType.DataEntity];

    private static readonly ArtifactType[] RequirementTypes =
    [
        ArtifactType.FunctionalRequirement, ArtifactType.NonFunctionalRequirement, ArtifactType.BusinessRule,
        ArtifactType.UserStory, ArtifactType.AcceptanceCriterion
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IAiReapDbContext _db;
    private readonly IUserRoleDirectory _roles;
    private readonly ICurrentUser _currentUser;

    public LifecycleEvidenceService(IAiReapDbContext db, IUserRoleDirectory roles, ICurrentUser currentUser)
    {
        _db = db;
        _roles = roles;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<StageEvidence>> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var result = await GetManyAsync([projectId], cancellationToken);
        return result.TryGetValue(projectId, out var stages) ? stages : [];
    }

    public async Task<IReadOnlyList<StageCheckEvidence>> GetDetailedAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var result = await EvaluateManyAsync([projectId], cancellationToken);
        return result.TryGetValue(projectId, out var stages) ? stages : [];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StageEvidence>>> GetManyAsync(
        IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
    {
        var detailed = await EvaluateManyAsync(projectIds, cancellationToken);
        return detailed.ToDictionary(
            p => p.Key,
            p => (IReadOnlyList<StageEvidence>)p.Value.Select(s => s.ToStageEvidence()).ToList());
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StageCheckEvidence>>> EvaluateManyAsync(
        IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken)
    {
        var ids = projectIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<StageCheckEvidence>>();
        }

        var projects = await _db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var artifacts = await _db.Artifacts.AsNoTracking().Where(a => ids.Contains(a.ProjectId)).ToListAsync(cancellationToken);
        var artifactIds = artifacts.Select(a => a.Id).ToList();
        var relationships = await _db.ArtifactRelationships.AsNoTracking()
            .Where(r => _db.Artifacts.Any(a => a.Id == r.SourceArtifactId && ids.Contains(a.ProjectId)))
            .ToListAsync(cancellationToken);
        var sourceCounts = await _db.RequirementSources
            .Where(s => ids.Contains(s.ProjectId))
            .GroupBy(s => s.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var stakeholderCounts = await _db.ProjectStakeholders
            .Where(s => ids.Contains(s.ProjectId))
            .GroupBy(s => s.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var operations = await _db.AIExecutions
            .Where(e => ids.Contains(e.ProjectId) && TrackedOperations.Contains(e.OperationType))
            .Select(e => new { e.ProjectId, e.OperationType })
            .Distinct()
            .ToListAsync(cancellationToken);
        var members = await _db.ProjectMembers
            .Where(m => ids.Contains(m.ProjectId))
            .Select(m => new { m.ProjectId, m.UserId })
            .ToListAsync(cancellationToken);
        var memberRoles = await _roles.GetActiveUserRolesAsync(members.Select(m => m.UserId).ToList(), cancellationToken);
        var openNotices = await _db.ArtifactImpactNotices
            .Where(n => ids.Contains(n.ProjectId) && n.AcknowledgedAt == null)
            .Select(n => new { n.ProjectId, n.AffectedArtifactId })
            .ToListAsync(cancellationToken);
        // §17 — a Developer's review, recorded against a specific artifact version.
        var developerReviews = await _db.ArtifactReviews
            .Where(r => artifactIds.Contains(r.ArtifactId) && r.ReviewerRole == Roles.Developer && r.VersionNumber != null)
            .Select(r => new { r.ArtifactId, r.VersionNumber })
            .ToListAsync(cancellationToken);
        var workRecords = await _db.ArtifactWorkRecords.AsNoTracking()
            .Where(w => artifactIds.Contains(w.ArtifactId))
            .ToListAsync(cancellationToken);

        var artifactsByProject = artifacts.GroupBy(a => a.ProjectId).ToDictionary(g => g.Key, g => g.ToList());
        var projectOf = artifacts.ToDictionary(a => a.Id, a => a.ProjectId);
        var relationshipsByProject = relationships
            .Where(r => projectOf.ContainsKey(r.SourceArtifactId))
            .GroupBy(r => projectOf[r.SourceArtifactId])
            .ToDictionary(g => g.Key, g => g.ToList());
        var latestOutcome = LatestOutcomes(workRecords);
        var reviewedVersions = developerReviews.Select(r => (r.ArtifactId, r.VersionNumber!.Value)).ToHashSet();

        return projects.ToDictionary(
            p => p.Id,
            p => Evaluate(new ProjectEvidence(
                p,
                artifactsByProject.GetValueOrDefault(p.Id) ?? [],
                relationshipsByProject.GetValueOrDefault(p.Id) ?? [],
                sourceCounts.GetValueOrDefault(p.Id),
                stakeholderCounts.GetValueOrDefault(p.Id),
                operations.Where(o => o.ProjectId == p.Id).Select(o => o.OperationType).ToHashSet(),
                members.Where(m => m.ProjectId == p.Id)
                    .Count(m => memberRoles.TryGetValue(m.UserId, out var roles) && roles.Contains(Roles.BusinessAnalyst)),
                openNotices.Where(n => n.ProjectId == p.Id).Select(n => n.AffectedArtifactId).ToHashSet(),
                reviewedVersions,
                latestOutcome),
                _currentUser.CanSee));
    }

    // The role whose record is normal lifecycle evidence for an outcome: a Developer for task
    // outcomes, QA for test outcomes.
    public static string EvidenceRole(WorkOutcome outcome) =>
        outcome is WorkOutcome.TaskCompleted or WorkOutcome.TaskReopened ? Roles.Developer : Roles.QA;

    // The latest record per artifact is its current outcome (records are append-only). Only records
    // made under the responsible role count: an Administrator may record work (it stays visible and
    // audited), but it is not Developer/QA evidence - bypassing missing evidence is the explicit
    // Administrator override on a stage transition.
    public static IReadOnlyDictionary<Guid, WorkOutcome> LatestOutcomes(IEnumerable<ArtifactWorkRecord> records) =>
        records
            .Where(w => w.RecordedByRole == EvidenceRole(w.Outcome))
            .GroupBy(w => w.ArtifactId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(w => w.RecordedAt).ThenByDescending(w => w.Id).First().Outcome);

    private sealed record ProjectEvidence(
        Project Project,
        List<Artifact> All,
        List<ArtifactRelationship> Relationships,
        int SourceCount,
        int StakeholderCount,
        HashSet<string> OperationsRun,
        int BusinessAnalystMembers,
        HashSet<Guid> OpenNoticeArtifactIds,
        HashSet<(Guid ArtifactId, int Version)> DeveloperReviewedVersions,
        IReadOnlyDictionary<Guid, WorkOutcome> LatestOutcome);

    // Pass/fail and counts are the same for every caller. Only the artifact codes quoted in a failing
    // check's detail follow ArtifactVisibility: an artifact the caller may not see (for a Developer,
    // an unapproved requirement) is counted but not named. The blocking artifacts behind a failing
    // check are kept (CheckEvidence.Blockers) for ExitCheckGuidanceService, which applies the same
    // visibility rule before any of them is returned.
    private static IReadOnlyList<StageCheckEvidence> Evaluate(ProjectEvidence e, Func<Artifact, bool> canSee)
    {
        var project = e.Project;
        var byId = e.All.ToDictionary(a => a.Id);
        var live = e.All.Where(a => a.Status != ArtifactStatus.Rejected).ToList();
        List<Artifact> Of(ArtifactType type) => live.Where(a => a.ArtifactType == type).ToList();

        var frs = Of(ArtifactType.FunctionalRequirement);
        var approvedFrs = frs.Where(f => ArtifactStatusTransitions.IsApprovedOrLater(f.Status)).ToList();
        var stories = Of(ArtifactType.UserStory);
        var criteria = Of(ArtifactType.AcceptanceCriterion);
        var designs = Of(ArtifactType.DesignArtifact);
        var tasks = Of(ArtifactType.ImplementationTask);
        var tests = Of(ArtifactType.TestCase);
        var designSet = live.Where(a => DesignTypes.Contains(a.ArtifactType)).ToList();

        var taskIds = tasks.Select(a => a.Id).ToHashSet();
        var testIds = tests.Select(a => a.Id).ToHashSet();
        List<Guid> TasksOf(Artifact fr) => e.Relationships
            .Where(r => r.RelationshipType == RelationshipType.Implements && r.TargetArtifactId == fr.Id && taskIds.Contains(r.SourceArtifactId))
            .Select(r => r.SourceArtifactId).ToList();
        List<Guid> TestsOf(Artifact fr) => e.Relationships
            .Where(r => r.RelationshipType == RelationshipType.TestedBy && r.SourceArtifactId == fr.Id && testIds.Contains(r.TargetArtifactId))
            .Select(r => r.TargetArtifactId).ToList();
        bool Is(Guid id, WorkOutcome outcome) => e.LatestOutcome.TryGetValue(id, out var o) && o == outcome;
        List<Artifact> Artifacts(IEnumerable<Guid> ids) => ids.Distinct().Select(id => byId[id]).ToList();

        CheckEvidence Ran(string key, string label, string operation, string what) =>
            e.OperationsRun.Contains(operation)
                ? Result(key, new(label, true, $"{what} has been run."))
                : Result(key, new(label, false, $"{what} has not been run yet."), CheckBlocker.Of(BlockerReasons.NotRun));

        CheckEvidence Recorded(string key, string label, string? value, string what) =>
            string.IsNullOrWhiteSpace(value)
                ? Result(key, new(label, false, $"The project has no {what} yet."), CheckBlocker.Of(BlockerReasons.Missing))
                : Result(key, new(label, true, $"{char.ToUpperInvariant(what[0])}{what[1..]} recorded."));

        var openQuestions = Of(ArtifactType.ClarificationQuestion).Where(q =>
            (JsonSerializer.Deserialize<ClarificationQuestionPayload>(q.DataJson, JsonOptions)?.ClarificationStatus ?? "Open") == "Open").ToList();

        // §15 — conflicts are never deleted; a human resolves one by rejecting one side of the pair.
        var conflictPairs = e.Relationships.Where(r =>
            r.RelationshipType is RelationshipType.ConflictsWith or RelationshipType.DuplicateOf
            && byId.TryGetValue(r.SourceArtifactId, out var a) && a.Status != ArtifactStatus.Rejected
            && byId.TryGetValue(r.TargetArtifactId, out var b) && b.Status != ArtifactStatus.Rejected).ToList();
        var unresolvedConflicts = conflictPairs.Count;
        CheckEvidence ConflictsResolved() => Result(ExitCheckKeys.ConflictsResolved, new(Labels.ConflictsResolved, unresolvedConflicts == 0,
            unresolvedConflicts == 0 ? "No unresolved conflicts or duplicates." : $"{unresolvedConflicts} conflict/duplicate pair(s) need a human decision."),
            ConflictBlocker(conflictPairs, byId));

        var objectives = Recorded(ExitCheckKeys.Objectives, Labels.Objectives, project.Objectives, "objectives");

        // Implementation evidence: every linked task completed by a Developer (work record), not merely approved.
        var implemented = approvedFrs.Where(fr => TasksOf(fr) is { Count: > 0 } linked && linked.All(t => Is(t, WorkOutcome.TaskCompleted))).ToList();
        var implementedIds = implemented.Select(fr => fr.Id).ToHashSet();
        var verifiedIds = implemented
            .Where(fr => TestsOf(fr) is { Count: > 0 } linked && linked.All(t => Is(t, WorkOutcome.TestPassed)))
            .Select(fr => fr.Id).ToHashSet();

        // Why an approved FR is not implemented: no task at all, or linked tasks not yet complete.
        Gap NotImplementedGap(Artifact fr, string waitingReason) =>
            TasksOf(fr) is { Count: > 0 } linked
                ? new(waitingReason, "waiting on", Artifacts(linked.Where(t => !Is(t, WorkOutcome.TaskCompleted))))
                : new(BlockerReasons.NoTask);

        return
        [
            Stage(ProjectStatus.Draft,
                Recorded(ExitCheckKeys.BusinessProblem, "Business problem recorded", project.BusinessProblem, "business problem"),
                objectives,
                Recorded(ExitCheckKeys.Scope, "Scope recorded", project.Scope, "scope"),
                Result(ExitCheckKeys.BusinessAnalystMember, new("Business Analyst on the project", e.BusinessAnalystMembers > 0,
                    e.BusinessAnalystMembers > 0 ? $"{e.BusinessAnalystMembers} active Business Analyst member(s)." : "No active Business Analyst is a member of this project."),
                    CheckBlocker.Of(BlockerReasons.Missing))),

            Stage(ProjectStatus.RequirementsGathering,
                Result(ExitCheckKeys.RawRequirements, new(Labels.RawRequirements, e.SourceCount > 0,
                    e.SourceCount > 0 ? $"{e.SourceCount} requirement source(s) recorded." : "No raw requirements entered or uploaded yet."),
                    CheckBlocker.Of(BlockerReasons.Missing)),
                objectives,
                Result(ExitCheckKeys.Stakeholders, new(Labels.Stakeholders, e.StakeholderCount > 0,
                    e.StakeholderCount > 0 ? $"{e.StakeholderCount} stakeholder(s) recorded." : "No stakeholders recorded yet."),
                    CheckBlocker.Of(BlockerReasons.Missing))),

            Stage(ProjectStatus.Analysis,
                Ran(ExitCheckKeys.AnalysisRun, Labels.AnalysisRun, AnalysisOperation, "Requirement analysis"),
                Result(ExitCheckKeys.QuestionsAnswered, new(Labels.QuestionsAnswered, openQuestions.Count == 0,
                    openQuestions.Count == 0 ? "No open clarification questions." : $"{openQuestions.Count} clarification question(s) still open."),
                    Group(openQuestions.Select(q => (q, (Gap?)new Gap(BlockerReasons.OpenQuestions))))),
                AllApproved(ExitCheckKeys.FrsApproved, Labels.FrsApproved, frs, "functional requirements"),
                // A project may genuinely have no NFRs or business rules: only the existing ones must be approved.
                AllApproved(ExitCheckKeys.NfrsApproved, Labels.NfrsApproved, Of(ArtifactType.NonFunctionalRequirement), "non-functional requirements", noneRequired: true),
                AllApproved(ExitCheckKeys.RulesApproved, Labels.RulesApproved, Of(ArtifactType.BusinessRule), "business rules", noneRequired: true),
                AllApproved(ExitCheckKeys.StoriesApproved, Labels.StoriesApproved, stories, "user stories"),
                AllApproved(ExitCheckKeys.CriteriaApproved, Labels.CriteriaApproved, criteria, "acceptance criteria"),
                Ran(ExitCheckKeys.QualityRun, Labels.QualityRun, QualityOperation, "Requirement quality analysis"),
                Ran(ExitCheckKeys.ConflictRun, Labels.ConflictRun, ConflictOperation, "Conflict detection"),
                ConflictsResolved(),
                Chain(ExitCheckKeys.StoryChain, "Every functional requirement has a story and acceptance criteria", frs, stories, criteria, designs, tasks, tests, e.Relationships, full: false, canSee)),

            Stage(ProjectStatus.Design,
                AllApproved(ExitCheckKeys.DesignApproved, Labels.DesignApproved, designs, "solution designs"),
                AllApproved(ExitCheckKeys.ApiApproved, Labels.ApiApproved, Of(ArtifactType.ApiSpecification), "API specifications"),
                AllApproved(ExitCheckKeys.DatabaseApproved, Labels.DatabaseApproved, Of(ArtifactType.DataEntity), "data entities"),
                DeveloperReviewed(designSet, e.DeveloperReviewedVersions, canSee),
                OpenNotices(ExitCheckKeys.DesignNotices, "No open impact notices on design", designSet, e.OpenNoticeArtifactIds, "design artifact(s)", canSee),
                BaselineIntact(live, canSee),
                SourceEvidence(live, canSee)),

            Stage(ProjectStatus.Approved,
                AllApproved(ExitCheckKeys.TasksApproved, Labels.TasksApproved, tasks, "implementation tasks"),
                Every(ExitCheckKeys.EveryFrHasTask, "Every approved functional requirement has a task", approvedFrs,
                    fr => TasksOf(fr).Count > 0 ? null : new Gap(BlockerReasons.NoTask),
                    "have no implementation task", "approved functional requirements", canSee, BlockerReasons.NoApprovedFrs)),

            Stage(ProjectStatus.Implementation,
                Every(ExitCheckKeys.TasksCompleted, "Implementation tasks completed", tasks,
                    t => Is(t.Id, WorkOutcome.TaskCompleted) ? null
                        // Work can only be recorded on an approved task (ArtifactWorkService).
                        : !ArtifactStatusTransitions.IsApprovedOrLater(t.Status) ? new Gap(BlockerReasons.NeedsApproval)
                        : Is(t.Id, WorkOutcome.TaskReopened) ? new Gap(BlockerReasons.TaskReopened)
                        : new Gap(BlockerReasons.NotCompleted),
                    "are not marked complete by a Developer", "implementation tasks", canSee),
                Count(ExitCheckKeys.FrsImplemented, Labels.FrsImplemented, approvedFrs, implemented.Count, "implemented",
                    fr => implementedIds.Contains(fr.Id) ? null : NotImplementedGap(fr, BlockerReasons.WaitingOnTasks))),

            Stage(ProjectStatus.Testing,
                AllApproved(ExitCheckKeys.TestsApproved, Labels.TestsApproved, tests, "test cases"),
                // Only approved requirements need tests; rejected or not-yet-approved ones are not in scope.
                Every(ExitCheckKeys.EveryFrHasApprovedTest, "Every approved functional requirement has an approved test case", approvedFrs,
                    fr =>
                    {
                        var linked = TestsOf(fr);
                        return linked.Any(t => ArtifactStatusTransitions.IsApprovedOrLater(byId[t].Status)) ? null
                            : linked.Count == 0 ? new Gap(BlockerReasons.NoTest)
                            : new Gap(BlockerReasons.TestNeedsApproval, "test awaiting approval:", Artifacts(linked));
                    },
                    "have no approved test case", "approved functional requirements", canSee, BlockerReasons.NoApprovedFrs),
                Every(ExitCheckKeys.TestsPassed, "Test cases passed", tests,
                    t => Is(t.Id, WorkOutcome.TestPassed) ? null
                        // A result can only be recorded on an approved test case (ArtifactWorkService).
                        : !ArtifactStatusTransitions.IsApprovedOrLater(t.Status) ? new Gap(BlockerReasons.NeedsApproval)
                        : Is(t.Id, WorkOutcome.TestFailed) ? new Gap(BlockerReasons.Failed)
                        : Is(t.Id, WorkOutcome.TestBlocked) ? new Gap(BlockerReasons.Blocked)
                        : new Gap(BlockerReasons.NoResult),
                    "have not passed (no QA outcome, failed or blocked)", "test cases", canSee),
                Count(ExitCheckKeys.FrsVerified, Labels.FrsVerified, approvedFrs, verifiedIds.Count, "verified",
                    fr => verifiedIds.Contains(fr.Id) ? null
                        : !implementedIds.Contains(fr.Id) ? NotImplementedGap(fr, BlockerReasons.NotImplemented)
                        : TestsOf(fr) is { Count: > 0 } linked
                            ? new Gap(BlockerReasons.TestsNotPassed, "waiting on", Artifacts(linked.Where(t => !Is(t, WorkOutcome.TestPassed))))
                            : new Gap(BlockerReasons.NoTest)),
                Chain(ExitCheckKeys.FullyTraced, Labels.FullyTraced, approvedFrs, stories, criteria, designs, tasks, tests, e.Relationships, full: true, canSee),
                OpenNotices(ExitCheckKeys.TestingNotices, "No open impact notices", live, e.OpenNoticeArtifactIds, "artifact(s)", canSee),
                ConflictsResolved(),
                SourceEvidence(live, canSee)),

            Stage(ProjectStatus.Completed),
        ];
    }

    // Why one artifact blocks a check: the reason (BlockerReasons), an optional note and related artifacts.
    private sealed record Gap(string Reason, string? Note = null, IReadOnlyList<Artifact>? Related = null);

    // Labels shared with the legacy SDLC progress view (SdlcProgressService) and its tests.
    internal static class Labels
    {
        public const string Objectives = "Business objectives recorded";
        public const string RawRequirements = "Raw requirements captured";
        public const string Stakeholders = "Stakeholders identified";
        public const string AnalysisRun = "AI requirement analysis run";
        public const string QuestionsAnswered = "Clarification questions answered";
        public const string FrsApproved = "Functional requirements approved";
        public const string NfrsApproved = "Non-functional requirements approved";
        public const string RulesApproved = "Business rules approved";
        public const string StoriesApproved = "User stories approved";
        public const string CriteriaApproved = "Acceptance criteria approved";
        public const string QualityRun = "Quality analysis run";
        public const string ConflictRun = "Conflict detection run";
        public const string ConflictsResolved = "Conflicts resolved";
        public const string DesignApproved = "Solution design approved";
        public const string ApiApproved = "API design approved";
        public const string DatabaseApproved = "Database design approved";
        public const string TasksApproved = "Implementation tasks approved";
        public const string FrsImplemented = "Functional requirements implemented";
        public const string TestsApproved = "Test cases approved";
        public const string FrsVerified = "Functional requirements verified";
        public const string FullyTraced = "Every approved functional requirement fully traced";
        public const string SourceEvidence = "Requirement source evidence and NFR targets accepted";
    }

    private static StageCheckEvidence Stage(ProjectStatus status, params CheckEvidence[] checks)
    {
        var stage = ProjectLifecyclePolicy.Stage(status);
        return new StageCheckEvidence(status, stage.Label, stage.OwnerRole, checks);
    }

    // A passed check carries no blockers.
    private static CheckEvidence Result(string key, SdlcCheck check, params CheckBlocker[] blockers) =>
        new(check, key, check.Passed ? [] : blockers);

    // One blocker per reason, in BlockerReasons.Order (earlier steps first), artifacts by code.
    private static CheckBlocker[] Group(IEnumerable<(Artifact Artifact, Gap? Gap)> gaps) =>
        gaps.Where(g => g.Gap is not null)
            .GroupBy(g => g.Gap!.Reason)
            .OrderBy(g => ReasonRank(g.Key))
            .Select(g => new CheckBlocker(g.Key, g
                .OrderBy(x => x.Artifact.Code)
                .Select(x => new BlockerArtifact(x.Artifact, x.Gap!.Note, x.Gap.Related))
                .ToList()))
            .ToArray();

    private static int ReasonRank(string reason)
    {
        for (var i = 0; i < BlockerReasons.Order.Count; i++)
        {
            if (BlockerReasons.Order[i] == reason) return i;
        }

        return int.MaxValue;
    }

    private static CheckEvidence AllApproved(string key, string label, IReadOnlyList<Artifact> items, string noun, bool noneRequired = false)
    {
        if (items.Count == 0)
        {
            return noneRequired
                ? Result(key, new(label, true, $"No {noun} recorded; none need approval."))
                : Result(key, new(label, false, $"No {noun} yet."), CheckBlocker.Of(BlockerReasons.NoneYet));
        }

        var pending = items.Where(a => !ArtifactStatusTransitions.IsApprovedOrLater(a.Status)).ToList();
        return pending.Count == 0
            ? Result(key, new(label, true, $"All {items.Count} {noun} approved."))
            : Result(key, new(label, false, $"{pending.Count} of {items.Count} {noun} still need approval."),
                Group(pending.Select(a => (a, (Gap?)new Gap(BlockerReasons.NeedsApproval, a.ApprovedVersion is null ? null : "edited since approval")))));
    }

    // `gap` returns null for an artifact that satisfies the check, else why it does not.
    private static CheckEvidence Every(string key, string label, IReadOnlyList<Artifact> items, Func<Artifact, Gap?> gap, string failure, string noun,
        Func<Artifact, bool> canSee, string emptyReason = BlockerReasons.NoneYet)
    {
        if (items.Count == 0)
        {
            return Result(key, new(label, false, $"No {noun} yet."), CheckBlocker.Of(emptyReason));
        }

        var gaps = items.Select(a => (Artifact: a, Gap: gap(a))).Where(x => x.Gap is not null).ToList();
        var bad = gaps.Select(x => x.Artifact).ToList();
        return bad.Count == 0
            ? Result(key, new(label, true, $"All {items.Count} {noun} satisfy this."))
            : Result(key, new(label, false, $"{bad.Count} of {items.Count} {failure}{Listing(bad, canSee)}."), Group(gaps));
    }

    private static CheckEvidence Count(string key, string label, IReadOnlyList<Artifact> frs, int reached, string word, Func<Artifact, Gap?> gap)
    {
        if (frs.Count == 0)
        {
            return Result(key, new(label, false, "No approved functional requirements yet."), CheckBlocker.Of(BlockerReasons.NoApprovedFrs));
        }

        return reached == frs.Count
            ? Result(key, new(label, true, $"All {frs.Count} functional requirements {word}."))
            : Result(key, new(label, false, $"{reached} of {frs.Count} functional requirements {word}."), Group(frs.Select(f => (f, gap(f)))));
    }

    // §17 — distinct from approval: a review recorded under the Developer role on the artifact's
    // current version. Editing the artifact creates a new version, which needs a new review.
    private static CheckEvidence DeveloperReviewed(IReadOnlyList<Artifact> designSet, HashSet<(Guid, int)> reviewed, Func<Artifact, bool> canSee)
    {
        const string label = "Developer review of the current design";
        if (designSet.Count == 0)
        {
            return Result(ExitCheckKeys.DeveloperReview, new(label, false, "No design artifacts yet."), CheckBlocker.Of(BlockerReasons.NoneYet));
        }

        var reviewedIds = reviewed.Select(r => r.Item1).ToHashSet();
        var missing = designSet.Where(a => !reviewed.Contains((a.Id, a.CurrentVersion))).ToList();
        return missing.Count == 0
            ? Result(ExitCheckKeys.DeveloperReview, new(label, true, $"A Developer has reviewed the current version of all {designSet.Count} design artifacts."))
            : Result(ExitCheckKeys.DeveloperReview,
                new(label, false, $"{missing.Count} of {designSet.Count} design artifacts have no Developer review of their current version{Listing(missing, canSee)}."),
                Group(missing.Select(a => (a, (Gap?)new Gap(BlockerReasons.NeedsDeveloperReview,
                    reviewedIds.Contains(a.Id) ? "edited since the last Developer review" : null)))));
    }

    private static CheckEvidence OpenNotices(string key, string label, IReadOnlyList<Artifact> scope, HashSet<Guid> open, string noun, Func<Artifact, bool> canSee)
    {
        var flagged = scope.Where(a => open.Contains(a.Id)).ToList();
        return flagged.Count == 0
            ? Result(key, new(label, true, "No unacknowledged impact notices."))
            : Result(key, new(label, false, $"{flagged.Count} {noun} have unacknowledged impact notices{Listing(flagged, canSee)}."),
                Group(flagged.Select(a => (a, (Gap?)new Gap(BlockerReasons.OpenNotice)))));
    }

    // The requirements approved during Analysis must not have been reopened (edited back into review).
    private static CheckEvidence BaselineIntact(IReadOnlyList<Artifact> live, Func<Artifact, bool> canSee)
    {
        const string label = "Requirements baseline still approved";
        var reopened = live
            .Where(a => RequirementTypes.Contains(a.ArtifactType) && !ArtifactStatusTransitions.IsApprovedOrLater(a.Status))
            .ToList();
        return reopened.Count == 0
            ? Result(ExitCheckKeys.Baseline, new(label, true, "Every requirement, rule, story and acceptance criterion is still approved."))
            : Result(ExitCheckKeys.Baseline, new(label, false, $"{reopened.Count} requirement artifact(s) are not approved{Listing(reopened, canSee)}."),
                // Never approved (e.g. added after Analysis) vs. approved, then edited back into review.
                Group(reopened.Select(a => (a, (Gap?)(a.ApprovedVersion is null
                    ? new Gap(BlockerReasons.NeedsApproval)
                    : new Gap(BlockerReasons.Reopened, "edited since approval"))))));
    }

    // G3 — every live FR and NFR, judged by RequirementApprovalEvidence (the rule artifact approval
    // also applies). Records made before evidence checks and derived/assumed requirements without a
    // quote do not block, but are named (subject to ArtifactVisibility) so they are never mistaken for
    // source-verified ones. Only codes and statuses are reported, never quote text.
    private static CheckEvidence SourceEvidence(IReadOnlyList<Artifact> live, Func<Artifact, bool> canSee)
    {
        var assessed = live
            .Where(a => RequirementApprovalEvidence.Applies(a.ArtifactType))
            .Select(a => (Artifact: a, Assessment: RequirementApprovalEvidence.Assess(a.ArtifactType, a.DataJson)))
            .ToList();
        if (assessed.Count == 0)
        {
            return Result(ExitCheckKeys.SourceEvidence, new(Labels.SourceEvidence, true, "No functional or non-functional requirements to check."));
        }

        string Noted(string what, Func<RequirementApprovalEvidence.Assessment, bool> which)
        {
            var items = assessed.Where(x => which(x.Assessment)).Select(x => x.Artifact).ToList();
            return items.Count == 0 ? "" : $" {items.Count} {what}{Listing(items, canSee)}.";
        }

        var notes = Noted("made before evidence checks (not verified)", a => a.EvidenceUnchecked)
            + Noted("derived or assumed with no source quote (not source-verified)", a => a.OriginWithoutQuote is not null);
        var blocked = assessed.Where(x => x.Assessment.Blocks).ToList();
        return blocked.Count == 0
            ? Result(ExitCheckKeys.SourceEvidence, new(Labels.SourceEvidence, true,
                $"No requirement has an unverified quote, an explicit claim without a quote or an unconfirmed NFR target.{notes}"))
            : Result(ExitCheckKeys.SourceEvidence, new(Labels.SourceEvidence, false,
                    $"{blocked.Count} of {assessed.Count} requirements need a decision on their source evidence or NFR target{Listing(blocked.Select(x => x.Artifact).ToList(), canSee)}.{notes}"),
                Group(blocked.Select(x => (x.Artifact, (Gap?)new Gap(x.Assessment.Blockers[0],
                    x.Assessment.Blockers.Count > 1 ? "also: " + string.Join("; ", x.Assessment.Blockers.Skip(1).Select(RequirementApprovalEvidence.Describe)) : null)))));
    }

    // Both sides of every unresolved conflict/duplicate pair, each noting the other side(s).
    private static CheckBlocker ConflictBlocker(List<ArtifactRelationship> pairs, Dictionary<Guid, Artifact> byId)
    {
        var entries = new Dictionary<Guid, (Artifact Artifact, string Note, List<Artifact> Related)>();
        void Add(Artifact artifact, Artifact other, string note)
        {
            if (!entries.TryGetValue(artifact.Id, out var entry))
            {
                entries[artifact.Id] = entry = (artifact, note, []);
            }

            entry.Related.Add(other);
        }

        // A fixed order (not the order rows were read in), so an artifact in several pairs always gets
        // the same note: the note is part of the failure identity an override covers (OverrideScope).
        foreach (var pair in pairs
                     .OrderBy(p => p.RelationshipType)
                     .ThenBy(p => byId[p.SourceArtifactId].Code, StringComparer.Ordinal)
                     .ThenBy(p => byId[p.TargetArtifactId].Code, StringComparer.Ordinal)
                     .ThenBy(p => p.Id))
        {
            var note = pair.RelationshipType == RelationshipType.DuplicateOf ? "possible duplicate of" : "conflicts with";
            Add(byId[pair.SourceArtifactId], byId[pair.TargetArtifactId], note);
            Add(byId[pair.TargetArtifactId], byId[pair.SourceArtifactId], note);
        }

        return new CheckBlocker(BlockerReasons.Unresolved, entries.Values
            .OrderBy(x => x.Artifact.Code)
            .Select(x => new BlockerArtifact(x.Artifact, x.Note, x.Related))
            .ToList());
    }

    // §21 — every functional requirement traced to a story and acceptance criteria (and, when `full`,
    // on to design, task and test). Same links as TraceabilityService.GetMatrixAsync, including its
    // source-level fallback for design artifacts.
    private static CheckEvidence Chain(
        string key, string label, List<Artifact> frs, List<Artifact> stories, List<Artifact> criteria, List<Artifact> designs,
        List<Artifact> tasks, List<Artifact> tests, List<ArtifactRelationship> relationships, bool full, Func<Artifact, bool> canSee)
    {
        if (frs.Count == 0)
        {
            return Result(key, new(label, false, "No functional requirements yet."),
                CheckBlocker.Of(full ? BlockerReasons.NoApprovedFrs : BlockerReasons.NoneYet));
        }

        var storyIds = stories.Select(a => a.Id).ToHashSet();
        var criterionIds = criteria.Select(a => a.Id).ToHashSet();
        var taskIds = tasks.Select(a => a.Id).ToHashSet();
        var testIds = tests.Select(a => a.Id).ToHashSet();

        var gaps = new List<(Artifact Fr, string Text, List<string> Missing)>();
        foreach (var fr in frs.OrderBy(f => f.Code))
        {
            var frStories = relationships
                .Where(r => r.RelationshipType == RelationshipType.DerivedFrom && r.TargetArtifactId == fr.Id && storyIds.Contains(r.SourceArtifactId))
                .Select(r => r.SourceArtifactId)
                .ToHashSet();

            var missing = new List<string>();
            if (frStories.Count == 0) missing.Add("user story");
            if (!relationships.Any(r => r.RelationshipType == RelationshipType.DerivedFrom && frStories.Contains(r.TargetArtifactId) && criterionIds.Contains(r.SourceArtifactId)))
                missing.Add("acceptance criteria");
            if (full)
            {
                if (!designs.Any(d => d.RequirementSourceId == fr.RequirementSourceId)) missing.Add("design");
                if (!relationships.Any(r => r.RelationshipType == RelationshipType.Implements && r.TargetArtifactId == fr.Id && taskIds.Contains(r.SourceArtifactId)))
                    missing.Add("task");
                if (!relationships.Any(r => r.RelationshipType == RelationshipType.TestedBy && r.SourceArtifactId == fr.Id && testIds.Contains(r.TargetArtifactId)))
                    missing.Add("test case");
            }

            if (missing.Count > 0)
            {
                gaps.Add((fr, $"{fr.Code} (no {string.Join(", ", missing)})", missing));
            }
        }

        if (gaps.Count == 0)
        {
            return Result(key, new(label, true, full
                ? $"All {frs.Count} functional requirements are traced end to end."
                : $"All {frs.Count} functional requirements have a story with acceptance criteria."));
        }

        var blockers = Group(gaps.Select(g => (g.Fr, (Gap?)(full
            ? new Gap(BlockerReasons.TraceGaps, $"no {string.Join(", ", g.Missing)}")
            : new Gap(g.Missing.Contains("user story") ? BlockerReasons.NoStory : BlockerReasons.NoCriteria)))));

        // Gaps on requirements the caller may not see are counted, never named.
        var visible = gaps.Where(g => canSee(g.Fr)).Select(g => g.Text).ToList();
        if (visible.Count == 0)
        {
            return Result(key, new(label, false, $"{gaps.Count} of {frs.Count} have gaps (not listed until approved)."), blockers);
        }

        var rest = gaps.Count - Math.Min(visible.Count, 3);
        var shown = string.Join("; ", visible.Take(3)) + (rest > 0 ? $"; and {rest} more" : "");
        return Result(key, new(label, false, $"{gaps.Count} of {frs.Count} have gaps: {shown}."), blockers);
    }

    // ": CODE, CODE and N more" naming only the artifacts the caller may see (ArtifactVisibility);
    // the rest are counted in "more", never named.
    private static string Listing(IReadOnlyList<Artifact> items, Func<Artifact, bool> canSee)
    {
        var codes = items.Where(canSee).OrderBy(a => a.Code).Select(a => a.Code).ToList();
        if (codes.Count == 0)
        {
            return items.Count == 0 ? "" : " (not listed until approved)";
        }

        var rest = items.Count - Math.Min(codes.Count, 5);
        return ": " + string.Join(", ", codes.Take(5)) + (rest > 0 ? $" and {rest} more" : "");
    }
}
