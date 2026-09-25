using System.Text.Json;
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

    public LifecycleEvidenceService(IAiReapDbContext db, IUserRoleDirectory roles)
    {
        _db = db;
        _roles = roles;
    }

    public async Task<IReadOnlyList<StageEvidence>> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var result = await GetManyAsync([projectId], cancellationToken);
        return result.TryGetValue(projectId, out var stages) ? stages : [];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StageEvidence>>> GetManyAsync(
        IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default)
    {
        var ids = projectIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<StageEvidence>>();
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
                latestOutcome)));
    }

    // The latest record per artifact is its current outcome (records are append-only).
    public static IReadOnlyDictionary<Guid, WorkOutcome> LatestOutcomes(IEnumerable<ArtifactWorkRecord> records) =>
        records
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

    private static IReadOnlyList<StageEvidence> Evaluate(ProjectEvidence e)
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

        SdlcCheck Ran(string label, string operation, string what) =>
            e.OperationsRun.Contains(operation)
                ? new(label, true, $"{what} has been run.")
                : new(label, false, $"{what} has not been run yet.");

        SdlcCheck Recorded(string label, string? value, string what) =>
            string.IsNullOrWhiteSpace(value)
                ? new(label, false, $"The project has no {what} yet.")
                : new(label, true, $"{char.ToUpperInvariant(what[0])}{what[1..]} recorded.");

        var openQuestions = Of(ArtifactType.ClarificationQuestion).Count(q =>
            (JsonSerializer.Deserialize<ClarificationQuestionPayload>(q.DataJson, JsonOptions)?.ClarificationStatus ?? "Open") == "Open");

        // §15 — conflicts are never deleted; a human resolves one by rejecting one side of the pair.
        var unresolvedConflicts = e.Relationships.Count(r =>
            r.RelationshipType is RelationshipType.ConflictsWith or RelationshipType.DuplicateOf
            && byId.TryGetValue(r.SourceArtifactId, out var a) && a.Status != ArtifactStatus.Rejected
            && byId.TryGetValue(r.TargetArtifactId, out var b) && b.Status != ArtifactStatus.Rejected);
        SdlcCheck ConflictsResolved() => new(Labels.ConflictsResolved, unresolvedConflicts == 0,
            unresolvedConflicts == 0 ? "No unresolved conflicts or duplicates." : $"{unresolvedConflicts} conflict/duplicate pair(s) need a human decision.");

        var objectives = Recorded(Labels.Objectives, project.Objectives, "objectives");

        // Implementation evidence: every linked task completed by a Developer (work record), not merely approved.
        var implemented = approvedFrs.Where(fr => TasksOf(fr) is { Count: > 0 } linked && linked.All(t => Is(t, WorkOutcome.TaskCompleted))).ToList();

        return
        [
            Stage(ProjectStatus.Draft,
                Recorded("Business problem recorded", project.BusinessProblem, "business problem"),
                objectives,
                Recorded("Scope recorded", project.Scope, "scope"),
                new("Business Analyst on the project", e.BusinessAnalystMembers > 0,
                    e.BusinessAnalystMembers > 0 ? $"{e.BusinessAnalystMembers} active Business Analyst member(s)." : "No active Business Analyst is a member of this project.")),

            Stage(ProjectStatus.RequirementsGathering,
                new(Labels.RawRequirements, e.SourceCount > 0,
                    e.SourceCount > 0 ? $"{e.SourceCount} requirement source(s) recorded." : "No raw requirements entered or uploaded yet."),
                objectives,
                new(Labels.Stakeholders, e.StakeholderCount > 0,
                    e.StakeholderCount > 0 ? $"{e.StakeholderCount} stakeholder(s) recorded." : "No stakeholders recorded yet.")),

            Stage(ProjectStatus.Analysis,
                Ran(Labels.AnalysisRun, AnalysisOperation, "Requirement analysis"),
                new(Labels.QuestionsAnswered, openQuestions == 0,
                    openQuestions == 0 ? "No open clarification questions." : $"{openQuestions} clarification question(s) still open."),
                AllApproved(Labels.FrsApproved, frs, "functional requirements"),
                // A project may genuinely have no NFRs or business rules: only the existing ones must be approved.
                AllApproved(Labels.NfrsApproved, Of(ArtifactType.NonFunctionalRequirement), "non-functional requirements", noneRequired: true),
                AllApproved(Labels.RulesApproved, Of(ArtifactType.BusinessRule), "business rules", noneRequired: true),
                AllApproved(Labels.StoriesApproved, stories, "user stories"),
                AllApproved(Labels.CriteriaApproved, criteria, "acceptance criteria"),
                Ran(Labels.QualityRun, QualityOperation, "Requirement quality analysis"),
                Ran(Labels.ConflictRun, ConflictOperation, "Conflict detection"),
                ConflictsResolved(),
                Chain("Every functional requirement has a story and acceptance criteria", frs, stories, criteria, designs, tasks, tests, e.Relationships, full: false)),

            Stage(ProjectStatus.Design,
                AllApproved(Labels.DesignApproved, designs, "solution designs"),
                AllApproved(Labels.ApiApproved, Of(ArtifactType.ApiSpecification), "API specifications"),
                AllApproved(Labels.DatabaseApproved, Of(ArtifactType.DataEntity), "data entities"),
                DeveloperReviewed(designSet, e.DeveloperReviewedVersions),
                OpenNotices("No open impact notices on design", designSet, e.OpenNoticeArtifactIds, "design artifact(s)"),
                BaselineIntact(live)),

            Stage(ProjectStatus.Approved,
                AllApproved(Labels.TasksApproved, tasks, "implementation tasks"),
                Every("Every approved functional requirement has a task", approvedFrs, fr => TasksOf(fr).Count > 0, "have no implementation task", "approved functional requirements")),

            Stage(ProjectStatus.Implementation,
                Every("Implementation tasks completed", tasks, t => Is(t.Id, WorkOutcome.TaskCompleted), "are not marked complete by a Developer", "implementation tasks"),
                Count(Labels.FrsImplemented, approvedFrs, implemented.Count, "implemented")),

            Stage(ProjectStatus.Testing,
                AllApproved(Labels.TestsApproved, tests, "test cases"),
                // Only approved requirements need tests; rejected or not-yet-approved ones are not in scope.
                Every("Every approved functional requirement has an approved test case", approvedFrs,
                    fr => TestsOf(fr).Any(t => ArtifactStatusTransitions.IsApprovedOrLater(byId[t].Status)), "have no approved test case", "approved functional requirements"),
                Every("Test cases passed", tests, t => Is(t.Id, WorkOutcome.TestPassed), "have not passed (no QA outcome, failed or blocked)", "test cases"),
                Count(Labels.FrsVerified, approvedFrs,
                    implemented.Count(fr => TestsOf(fr) is { Count: > 0 } linked && linked.All(t => Is(t, WorkOutcome.TestPassed))), "verified"),
                Chain(Labels.FullyTraced, approvedFrs, stories, criteria, designs, tasks, tests, e.Relationships, full: true),
                OpenNotices("No open impact notices", live, e.OpenNoticeArtifactIds, "artifact(s)"),
                ConflictsResolved()),

            Stage(ProjectStatus.Completed),
        ];
    }

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
    }

    private static StageEvidence Stage(ProjectStatus status, params SdlcCheck[] checks)
    {
        var stage = ProjectLifecyclePolicy.Stage(status);
        return new StageEvidence(status, stage.Label, stage.OwnerRole, checks);
    }

    private static SdlcCheck AllApproved(string label, IReadOnlyList<Artifact> items, string noun, bool noneRequired = false)
    {
        if (items.Count == 0)
        {
            return noneRequired ? new(label, true, $"No {noun} recorded; none need approval.") : new(label, false, $"No {noun} yet.");
        }

        var pending = items.Count(a => !ArtifactStatusTransitions.IsApprovedOrLater(a.Status));
        return pending == 0
            ? new(label, true, $"All {items.Count} {noun} approved.")
            : new(label, false, $"{pending} of {items.Count} {noun} still need approval.");
    }

    private static SdlcCheck Every(string label, IReadOnlyList<Artifact> items, Func<Artifact, bool> ok, string failure, string noun)
    {
        if (items.Count == 0)
        {
            return new(label, false, $"No {noun} yet.");
        }

        var bad = items.Where(a => !ok(a)).OrderBy(a => a.Code).Select(a => a.Code).ToList();
        return bad.Count == 0
            ? new(label, true, $"All {items.Count} {noun} satisfy this.")
            : new(label, false, $"{bad.Count} of {items.Count} {failure}: {Shown(bad)}.");
    }

    private static SdlcCheck Count(string label, IReadOnlyList<Artifact> frs, int reached, string word)
    {
        if (frs.Count == 0)
        {
            return new(label, false, "No approved functional requirements yet.");
        }

        return reached == frs.Count
            ? new(label, true, $"All {frs.Count} functional requirements {word}.")
            : new(label, false, $"{reached} of {frs.Count} functional requirements {word}.");
    }

    // §17 — distinct from approval: a review recorded under the Developer role on the artifact's
    // current version. Editing the artifact creates a new version, which needs a new review.
    private static SdlcCheck DeveloperReviewed(IReadOnlyList<Artifact> designSet, HashSet<(Guid, int)> reviewed)
    {
        const string label = "Developer review of the current design";
        if (designSet.Count == 0)
        {
            return new(label, false, "No design artifacts yet.");
        }

        var missing = designSet.Where(a => !reviewed.Contains((a.Id, a.CurrentVersion))).OrderBy(a => a.Code).Select(a => a.Code).ToList();
        return missing.Count == 0
            ? new(label, true, $"A Developer has reviewed the current version of all {designSet.Count} design artifacts.")
            : new(label, false, $"{missing.Count} of {designSet.Count} design artifacts have no Developer review of their current version: {Shown(missing)}.");
    }

    private static SdlcCheck OpenNotices(string label, IReadOnlyList<Artifact> scope, HashSet<Guid> open, string noun)
    {
        var flagged = scope.Where(a => open.Contains(a.Id)).OrderBy(a => a.Code).Select(a => a.Code).ToList();
        return flagged.Count == 0
            ? new(label, true, "No unacknowledged impact notices.")
            : new(label, false, $"{flagged.Count} {noun} have unacknowledged impact notices: {Shown(flagged)}.");
    }

    // The requirements approved during Analysis must not have been reopened (edited back into review).
    private static SdlcCheck BaselineIntact(IReadOnlyList<Artifact> live)
    {
        const string label = "Requirements baseline still approved";
        var reopened = live
            .Where(a => RequirementTypes.Contains(a.ArtifactType) && !ArtifactStatusTransitions.IsApprovedOrLater(a.Status))
            .OrderBy(a => a.Code).Select(a => a.Code).ToList();
        return reopened.Count == 0
            ? new(label, true, "Every requirement, rule, story and acceptance criterion is still approved.")
            : new(label, false, $"{reopened.Count} requirement artifact(s) are not approved: {Shown(reopened)}.");
    }

    // §21 — every functional requirement traced to a story and acceptance criteria (and, when `full`,
    // on to design, task and test). Same links as TraceabilityService.GetMatrixAsync, including its
    // source-level fallback for design artifacts.
    private static SdlcCheck Chain(
        string label, List<Artifact> frs, List<Artifact> stories, List<Artifact> criteria, List<Artifact> designs,
        List<Artifact> tasks, List<Artifact> tests, List<ArtifactRelationship> relationships, bool full)
    {
        if (frs.Count == 0)
        {
            return new(label, false, "No functional requirements yet.");
        }

        var storyIds = stories.Select(a => a.Id).ToHashSet();
        var criterionIds = criteria.Select(a => a.Id).ToHashSet();
        var taskIds = tasks.Select(a => a.Id).ToHashSet();
        var testIds = tests.Select(a => a.Id).ToHashSet();

        var gaps = new List<string>();
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
                gaps.Add($"{fr.Code} (no {string.Join(", ", missing)})");
            }
        }

        if (gaps.Count == 0)
        {
            return new(label, true, full
                ? $"All {frs.Count} functional requirements are traced end to end."
                : $"All {frs.Count} functional requirements have a story with acceptance criteria.");
        }

        var shown = string.Join("; ", gaps.Take(3)) + (gaps.Count > 3 ? $"; and {gaps.Count - 3} more" : "");
        return new(label, false, $"{gaps.Count} of {frs.Count} have gaps: {shown}.");
    }

    private static string Shown(List<string> codes) =>
        string.Join(", ", codes.Take(5)) + (codes.Count > 5 ? $" and {codes.Count - 5} more" : "");
}
