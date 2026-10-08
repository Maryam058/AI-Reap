using System.Text.Json;
using System.Text.RegularExpressions;
using AiReap.Application.Agents;
using AiReap.Application.Common;
using AiReap.Application.Knowledge;
using AiReap.Application.Lifecycle;
using AiReap.Application.Persistence;
using AiReap.Application.Traceability;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.LifecycleReview;

// Builds the lifecycle review from what is already recorded; writes nothing. Every timestamped event
// is attributed to the stage the project was in at that moment, using the stage windows of the
// Completed rows in ProjectStageTransitions (a gated stage is left when its request is decided, not
// when it is submitted). Events before the first recorded window get no stage rather than a guess.
//
// Visibility follows the rest of the API: a caller limited by ArtifactVisibility sees only the
// artifacts (and the versions, reviews, work, links and AI executions of the artifacts) they may see,
// and no raw-source file names.
public partial class LifecycleReviewService : ILifecycleReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IProjectLifecycleService _lifecycle;
    private readonly ITraceabilityService _traceability;

    public LifecycleReviewService(
        IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IProjectLifecycleService lifecycle, ITraceabilityService traceability)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _lifecycle = lifecycle;
        _traceability = traceability;
    }

    // The stage whose exit checks consume each artifact type (LifecycleEvidenceService): where the
    // artifact "belongs" in the lifecycle, independent of when it happened to be created.
    public static ProjectStatus ServesStage(ArtifactType type) => type switch
    {
        ArtifactType.BusinessObjective => ProjectStatus.Draft,
        ArtifactType.DesignArtifact or ArtifactType.ApiSpecification or ArtifactType.DataEntity => ProjectStatus.Design,
        ArtifactType.ImplementationTask => ProjectStatus.Approved,
        ArtifactType.TestCase => ProjectStatus.Testing,
        _ => ProjectStatus.Analysis
    };

    public async Task<LifecycleReviewResponse> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} not found.");
        await _projectAccess.EnsureMemberAsync(projectId, cancellationToken);

        var history = await _lifecycle.GetHistoryAsync(projectId, cancellationToken);
        var windows = Windows(history);
        ProjectStatus? StageAt(DateTime at) => windows.LastOrDefault(w => w.Start <= at)?.Stage;

        // ---- load (visibility-filtered) --------------------------------------------------------

        var artifacts = await _db.Artifacts.AsNoTracking().VisibleTo(_currentUser)
            .Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Code)
            .ToListAsync(cancellationToken);
        var byId = artifacts.ToDictionary(a => a.Id);
        bool Visible(Guid id) => byId.ContainsKey(id);

        var versions = (await _db.ArtifactVersions.AsNoTracking()
                .Where(v => v.Artifact!.ProjectId == projectId)
                .Select(v => new { v.ArtifactId, v.VersionNumber, v.ChangedByUserId, v.ChangedAt, v.Origin })
                .ToListAsync(cancellationToken))
            .Where(v => Visible(v.ArtifactId)).ToList();

        var reviews = (await _db.ArtifactReviews.AsNoTracking()
                .Where(r => r.Artifact!.ProjectId == projectId)
                .ToListAsync(cancellationToken))
            .Where(r => Visible(r.ArtifactId)).ToList();

        var projectArtifactIds = _db.Artifacts.Where(a => a.ProjectId == projectId).Select(a => a.Id);
        var work = (await _db.ArtifactWorkRecords.AsNoTracking()
                .Where(w => projectArtifactIds.Contains(w.ArtifactId))
                .ToListAsync(cancellationToken))
            .Where(w => Visible(w.ArtifactId)).ToList();

        var relationships = (await _db.ArtifactRelationships.AsNoTracking()
                .Where(r => projectArtifactIds.Contains(r.SourceArtifactId))
                .ToListAsync(cancellationToken))
            .Where(r => Visible(r.SourceArtifactId) && Visible(r.TargetArtifactId)).ToList();

        // Copilot questions are AI activity that produces nothing in the lifecycle. A restricted caller
        // sees only executions that produced an artifact they can see (as in AuditTrailService).
        var restricted = _currentUser.SeesOnlyApprovedArtifacts();
        var executions = (await _db.AIExecutions.AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.OperationType != CopilotService.OperationType)
                .Select(e => new { e.Id, e.OperationType, e.Model, e.PromptTemplateVersion, e.UserId, e.Timestamp, e.Accepted, e.ProducedArtifactId, e.InputReference })
                .ToListAsync(cancellationToken))
            .Where(e => e.ProducedArtifactId is { } id ? Visible(id) : !restricted)
            .ToList();

        var runs = await _db.AgentRuns.AsNoTracking().Include(r => r.Stages)
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(cancellationToken);

        var sources = await _db.RequirementSources.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.SourceType, s.OriginalFileName, s.CreatedByUserId, s.CreatedAt })
            .ToListAsync(cancellationToken);

        var matrix = await _traceability.GetMatrixAsync(projectId, cancellationToken);

        // ---- per-artifact provenance ------------------------------------------------------------

        // The agent step whose output first listed the artifact (AgentOutput.ProducedArtifacts).
        var agentOrigin = new Dictionary<Guid, (Guid RunId, AgentKind Agent)>();
        foreach (var run in runs)
        {
            foreach (var step in run.Stages.Where(s => s.FinishedAt != null).OrderBy(s => s.FinishedAt))
            {
                foreach (var produced in ProducedBy(step))
                {
                    agentOrigin.TryAdd(produced, (run.Id, step.Agent));
                }
            }
        }

        var firstExecution = executions
            .Where(e => e.ProducedArtifactId != null)
            .GroupBy(e => e.ProducedArtifactId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Timestamp).First());
        var versionCount = versions.GroupBy(v => v.ArtifactId).ToDictionary(g => g.Key, g => g.Count());
        var reviewsByArtifact = reviews.GroupBy(r => r.ArtifactId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReviewedAt).ToList());
        var latestWork = work.GroupBy(w => w.ArtifactId).ToDictionary(g => g.Key, g => g.OrderBy(w => w.RecordedAt).Last());

        List<ReviewedArtifactLink> LinksOf(Guid id) => relationships
            .Where(r => r.SourceArtifactId == id || r.TargetArtifactId == id)
            .Select(r =>
            {
                var outgoing = r.SourceArtifactId == id;
                var other = byId[outgoing ? r.TargetArtifactId : r.SourceArtifactId];
                return new ReviewedArtifactLink(other.Id, other.Code, other.ArtifactType, r.RelationshipType, outgoing);
            })
            .OrderBy(l => l.IsOutgoing ? 0 : 1).ThenBy(l => l.Code)
            .ToList();

        var reviewed = artifacts.Select(a =>
        {
            ArtifactAiProvenance? ai = null;
            if (firstExecution.TryGetValue(a.Id, out var e))
            {
                var fromAgent = agentOrigin.TryGetValue(a.Id, out var o) ? o : ((Guid, AgentKind)?)null;
                ai = new ArtifactAiProvenance(e.Id, e.OperationType, e.Model, e.PromptTemplateVersion, e.UserId, e.Timestamp, e.Accepted,
                    fromAgent?.Item1, fromAgent?.Item2);
            }

            var artifactReviews = reviewsByArtifact.GetValueOrDefault(a.Id) ?? [];
            var lastReview = artifactReviews.LastOrDefault();
            var lastWork = latestWork.GetValueOrDefault(a.Id);
            return new ReviewedArtifact(
                a.Id, a.Code, a.Title, a.ArtifactType, a.Status, a.Origin, a.CurrentVersion, a.ApprovedVersion,
                versionCount.GetValueOrDefault(a.Id),
                a.CreatedByUserId, a.CreatedAt, a.UpdatedByUserId, a.UpdatedAt,
                StageAt(a.CreatedAt), ServesStage(a.ArtifactType), a.RequirementSourceId, ai,
                lastReview is null ? null : new ArtifactReviewSummary(lastReview.ReviewerUserId, lastReview.ReviewerRole, lastReview.Decision,
                    lastReview.VersionNumber, lastReview.Comment, lastReview.ReviewedAt),
                artifactReviews.Count,
                lastWork is null ? null : new ArtifactWorkSummary(lastWork.Outcome, lastWork.VersionNumber, lastWork.RecordedByUserId,
                    lastWork.RecordedByRole, lastWork.RecordedAt, lastWork.Note),
                LinksOf(a.Id));
        }).ToList();

        // ---- stages -----------------------------------------------------------------------------

        var aiCalls = executions
            .GroupBy(e => (e.OperationType, e.Timestamp, e.InputReference))
            .Select(g => new { g.Key.OperationType, g.Key.Timestamp, g.First().UserId, g.First().Model, Produced = g.Where(e => e.ProducedArtifactId != null).Select(e => e.ProducedArtifactId!.Value).ToList() })
            .ToList();
        var agentSteps = runs.SelectMany(r => r.Stages.Where(s => s.StartedAt != null).Select(s => new { Run = r, Step = s })).ToList();
        var currentIndex = ProjectLifecyclePolicy.IndexOf(project.Status);

        var stages = ProjectLifecyclePolicy.Stages.Select(stage =>
        {
            var index = ProjectLifecyclePolicy.IndexOf(stage.Status);
            var state = index < currentIndex || (index == currentIndex && ProjectLifecyclePolicy.IsFinal(stage.Status))
                ? StageReviewState.Completed
                : index == currentIndex ? StageReviewState.Current : StageReviewState.NotReached;

            var visits = windows.Where(w => w.Stage == stage.Status)
                .Select(w => new StageVisit(w.Start, w.End, w.EnteredBy.Kind, w.EnteredBy.DecidedByUserId ?? w.EnteredBy.RequestedByUserId,
                    w.EnteredBy.DecidedByRole ?? w.EnteredBy.RequestedByRole, w.LeftBy?.Kind))
                .ToList();
            bool In(DateTime at) => StageAt(at) == stage.Status;

            var created = reviewed.Where(a => a.CreatedInStage == stage.Status).ToList();
            var createdIds = created.Select(a => a.Id).ToHashSet();
            var stageVersions = versions.Where(v => v.VersionNumber > 1 && In(v.ChangedAt)).ToList();
            var stageReviews = reviews.Where(r => In(r.ReviewedAt)).ToList();
            var stageWork = work.Where(w => In(w.RecordedAt)).ToList();
            var touched = stageVersions.Select(v => v.ArtifactId)
                .Concat(stageReviews.Select(r => r.ArtifactId))
                .Concat(stageWork.Select(w => w.ArtifactId))
                .Where(id => !createdIds.Contains(id))
                .Distinct()
                .OrderBy(id => byId[id].Code)
                .ToList();

            var activity = new StageActivity(
                created.Count,
                created.Count(a => a.Origin == ArtifactOrigin.Ai),
                created.Count(a => a.Origin == ArtifactOrigin.Human),
                stageVersions.Select(v => v.ArtifactId).Distinct().Count(),
                stageVersions.Count,
                stageReviews.Count(r => r.Decision == ReviewDecision.Approved),
                stageReviews.Count(r => r.Decision == ReviewDecision.Rejected),
                stageReviews.Count(r => r.Decision == ReviewDecision.CommentOnly),
                aiCalls.Count(c => In(c.Timestamp)),
                agentSteps.Count(s => In(s.Step.StartedAt!.Value)),
                stageWork.Count(w => w.Outcome == WorkOutcome.TaskCompleted),
                stageWork.Count(w => w.Outcome == WorkOutcome.TestPassed),
                stageWork.Count(w => w.Outcome == WorkOutcome.TestFailed),
                stageWork.Count(w => w.Outcome == WorkOutcome.TestBlocked),
                sources.Count(s => In(s.CreatedAt)),
                touched);

            var transitions = history
                .Where(t => t.FromStatus == stage.Status || (t.Kind == StageTransitionKind.Baseline && t.ToStatus == stage.Status))
                .ToList();

            DateTime? completedAt = state != StageReviewState.Completed
                ? null
                : ProjectLifecyclePolicy.IsFinal(stage.Status)
                    ? visits.FirstOrDefault()?.EnteredAt
                    : visits.LastOrDefault(v => v.LeftBy == StageTransitionKind.Advance)?.LeftAt;

            return new StageReviewSummary(
                stage.Status, stage.Label, stage.OwnerRole, state, visits,
                visits.FirstOrDefault()?.EnteredAt, completedAt,
                state != StageReviewState.NotReached && visits.Count == 0,
                created.Select(a => a.Id).ToList(), CountByType(created), activity, transitions);
        }).ToList();

        // ---- totals -----------------------------------------------------------------------------

        int LatestOutcome(WorkOutcome outcome) => latestWork.Values.Count(w => w.Outcome == outcome);
        var fullyTraced = matrix.Count(r => r.UserStories.Count > 0 && r.AcceptanceCriteria.Count > 0
                                            && r.ImplementationTasks.Count > 0 && r.TestCases.Count > 0);
        var totals = new LifecycleReviewTotals(
            reviewed.Count,
            reviewed.Count(a => a.Origin == ArtifactOrigin.Ai),
            reviewed.Count(a => a.Origin == ArtifactOrigin.Human),
            reviewed.Count(a => a.CreatedInStage is null),
            CountByType(reviewed),
            versions.Count,
            reviews.Count,
            aiCalls.Count,
            runs.Count,
            LatestOutcome(WorkOutcome.TaskCompleted),
            LatestOutcome(WorkOutcome.TestPassed),
            LatestOutcome(WorkOutcome.TestFailed),
            LatestOutcome(WorkOutcome.TestBlocked),
            matrix.Count,
            fullyTraced,
            matrix.Count == 0 ? null : (int)Math.Round(100.0 * fullyTraced / matrix.Count));

        var canReadSources = _currentUser.CanReadRawSources();
        var reviewedSources = sources
            .Select(s => new ReviewedRequirementSource(s.Id, s.SourceType, canReadSources ? s.OriginalFileName : null, s.CreatedByUserId, s.CreatedAt, StageAt(s.CreatedAt)))
            .ToList();

        // ---- timeline ---------------------------------------------------------------------------

        var timeline = new List<ReviewTimelineEvent>();
        void Add(DateTime at, TimelineEventKind kind, string title, string? detail, string? userId, string? role, int count = 1, IReadOnlyList<Guid>? ids = null) =>
            timeline.Add(new ReviewTimelineEvent(at, kind, title, detail, userId, role, StageAt(at), count, ids ?? []));

        Add(project.CreatedAt, TimelineEventKind.ProjectCreated, "Project created", project.Name, project.CreatedByUserId, null);

        foreach (var t in history)
        {
            var from = t.FromStatus is { } f ? ProjectLifecyclePolicy.Stage(f).Label : null;
            var to = ProjectLifecyclePolicy.Stage(t.ToStatus).Label;
            if (t.Kind == StageTransitionKind.Baseline)
            {
                if (t.RequestedByUserId == ProjectStageTransition.SystemUserId)
                {
                    Add(t.RequestedAt, TimelineEventKind.StageTransition, $"Lifecycle tracking started at {to}", t.RequestNote, null, null);
                }

                continue;
            }

            if (t.Kind == StageTransitionKind.Rollback)
            {
                Add(t.RequestedAt, TimelineEventKind.StageTransition, $"Rolled back from {from} to {to}", t.RequestNote, t.RequestedByUserId, t.RequestedByRole);
                continue;
            }

            var gated = t.DecidedAt is not null && t.DecidedByUserId != t.RequestedByUserId;
            var overrideNote = t.IsOverride ? $"Override: {t.OverrideReason}" : null;
            if (!gated && t.State == StageTransitionState.Completed)
            {
                Add(t.RequestedAt, TimelineEventKind.StageTransition, $"Advanced from {from} to {to}", JoinText(t.RequestNote, overrideNote), t.RequestedByUserId, t.RequestedByRole);
                continue;
            }

            Add(t.RequestedAt, TimelineEventKind.StageTransition, $"Requested approval to move from {from} to {to}", t.RequestNote, t.RequestedByUserId, t.RequestedByRole);
            if (t.DecidedAt is { } decidedAt)
            {
                var verb = t.State switch
                {
                    StageTransitionState.Completed => "Approved",
                    StageTransitionState.Rejected => "Rejected",
                    StageTransitionState.Withdrawn => "Withdrew",
                    _ => "Decided"
                };
                Add(decidedAt, TimelineEventKind.StageTransition, $"{verb} the move from {from} to {to}",
                    JoinText(t.DecisionReason, overrideNote), t.DecidedByUserId, t.DecidedByRole);
            }
        }

        foreach (var s in reviewedSources)
        {
            Add(s.CreatedAt, TimelineEventKind.RequirementSourceAdded, "Requirement source added",
                s.FileName ?? s.SourceType.ToString(), s.CreatedByUserId, null);
        }

        foreach (var call in aiCalls)
        {
            var produced = call.Produced.Where(Visible).ToList();
            var title = produced.Count == 0
                ? $"AI ran {Humanize(call.OperationType)}"
                : $"AI generated {Describe(produced.Select(id => byId[id].ArtifactType).ToList())}";
            Add(call.Timestamp, TimelineEventKind.AiGeneration, title, $"{Humanize(call.OperationType)} · {call.Model}", call.UserId, null,
                Math.Max(produced.Count, 1), produced.OrderBy(id => byId[id].Code).ToList());
        }

        foreach (var s in agentSteps)
        {
            var name = Humanize(s.Step.Agent.ToString()) + " agent";
            Add(s.Step.StartedAt!.Value, TimelineEventKind.AgentStep, $"{name} ran", s.Step.Status.ToString(), s.Run.StartedByUserId, null);
            if (s.Step.DecidedAt is { } decided)
            {
                var verb = s.Step.Status == AgentStageStatus.Rejected ? "rejected" : "approved";
                Add(decided, TimelineEventKind.AgentStep, $"{name} output {verb}", s.Step.DecisionComment, s.Step.DecidedByUserId, null);
            }
        }

        // Artifacts with no AI execution (manual ones, or AI ones from before executions were logged).
        foreach (var g in reviewed.Where(a => a.Ai is null)
                     .GroupBy(a => (a.CreatedByUserId, a.Origin, Minute(a.CreatedAt))))
        {
            var items = g.ToList();
            var who = g.Key.Origin == ArtifactOrigin.Ai ? "AI generated" : "Created";
            Add(items.Min(a => a.CreatedAt), TimelineEventKind.ArtifactsCreated, $"{who} {Describe(items.Select(a => a.ArtifactType).ToList())}",
                Codes(items.Select(a => a.Code)), g.Key.CreatedByUserId, null, items.Count, items.Select(a => a.Id).ToList());
        }

        foreach (var g in versions.Where(v => v.VersionNumber > 1).GroupBy(v => (v.ChangedByUserId, v.Origin, Minute(v.ChangedAt))))
        {
            var ids = g.Select(v => v.ArtifactId).Distinct().ToList();
            var verb = g.Key.Origin == ArtifactOrigin.Ai ? "AI revised" : "Edited";
            Add(g.Min(v => v.ChangedAt), TimelineEventKind.ArtifactsEdited, $"{verb} {Describe(ids.Select(id => byId[id].ArtifactType).ToList())}",
                Codes(ids.Select(id => byId[id].Code)), g.Key.ChangedByUserId, null, g.Count(), ids);
        }

        foreach (var g in reviews.GroupBy(r => (r.ReviewerUserId, r.ReviewerRole, r.Decision, Minute(r.ReviewedAt))))
        {
            var ids = g.Select(r => r.ArtifactId).Distinct().ToList();
            var verb = g.Key.Decision switch
            {
                ReviewDecision.Approved => "Approved",
                ReviewDecision.Rejected => "Rejected",
                _ => "Commented on"
            };
            var comments = g.Select(r => r.Comment).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Take(2).ToList();
            Add(g.Min(r => r.ReviewedAt), TimelineEventKind.ArtifactsReviewed, $"{verb} {Describe(ids.Select(id => byId[id].ArtifactType).ToList())}",
                JoinText(Codes(ids.Select(id => byId[id].Code)), comments.Count > 0 ? string.Join(" · ", comments) : null),
                g.Key.ReviewerUserId, g.Key.ReviewerRole, g.Count(), ids);
        }

        foreach (var g in work.GroupBy(w => (w.RecordedByUserId, w.RecordedByRole, w.Outcome, Minute(w.RecordedAt))))
        {
            var ids = g.Select(w => w.ArtifactId).Distinct().ToList();
            var title = g.Key.Outcome switch
            {
                WorkOutcome.TaskCompleted => $"Completed {Plural(ids.Count, "implementation task")}",
                WorkOutcome.TaskReopened => $"Reopened {Plural(ids.Count, "implementation task")}",
                WorkOutcome.TestPassed => $"Recorded {Plural(ids.Count, "passed test")}",
                WorkOutcome.TestFailed => $"Recorded {Plural(ids.Count, "failed test")}",
                _ => $"Recorded {Plural(ids.Count, "blocked test")}"
            };
            Add(g.Min(w => w.RecordedAt), TimelineEventKind.WorkRecorded, title, Codes(ids.Select(id => byId[id].Code)),
                g.Key.RecordedByUserId, g.Key.RecordedByRole, g.Count(), ids);
        }

        var finalStage = history.LastOrDefault(t => t.State == StageTransitionState.Completed && t.ToStatus == ProjectStatus.Completed);
        return new LifecycleReviewResponse(
            project.Id, project.Name, project.Status,
            ProjectLifecyclePolicy.IsFinal(project.Status),
            ProjectLifecyclePolicy.IsFinal(project.Status) ? finalStage?.DecidedAt ?? finalStage?.RequestedAt : null,
            HistoryComplete: history.FirstOrDefault()?.RequestedByUserId is { } first && first != ProjectStageTransition.SystemUserId,
            new ProjectDefinitionReview(project.Description, project.BusinessProblem, project.Objectives, project.Scope,
                project.CreatedByUserId, project.CreatedAt, project.UpdatedAt),
            totals, stages, reviewed, reviewedSources,
            timeline.OrderBy(e => e.At).ThenBy(e => e.Kind).ToList(),
            matrix);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private sealed record Window(ProjectStatus Stage, DateTime Start, DateTime? End, StageTransitionResponse EnteredBy, StageTransitionResponse? LeftBy);

    // One window per completed move (and the baseline): the project was in ToStatus from the moment
    // the move took effect until the next completed move.
    private static List<Window> Windows(IReadOnlyList<StageTransitionResponse> history)
    {
        var moves = history
            .Where(t => t.State == StageTransitionState.Completed)
            .OrderBy(t => t.Sequence)
            .ToList();
        return moves.Select((t, i) =>
        {
            var next = i + 1 < moves.Count ? moves[i + 1] : null;
            return new Window(t.ToStatus, Effective(t), next is null ? null : Effective(next), t, next);
        }).ToList();
    }

    private static DateTime Effective(StageTransitionResponse t) => t.DecidedAt ?? t.RequestedAt;

    private static IEnumerable<Guid> ProducedBy(AgentStageRun step)
    {
        if (string.IsNullOrWhiteSpace(step.OutputJson) || step.OutputJson == "{}")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<AgentOutput>(step.OutputJson, JsonOptions)?.ProducedArtifacts.Select(a => a.Id) ?? [];
        }
        catch (JsonException)
        {
            // An unreadable output just means no agent attribution for its artifacts.
            return [];
        }
    }

    private static List<ArtifactTypeCount> CountByType(IEnumerable<ReviewedArtifact> artifacts) => artifacts
        .GroupBy(a => a.ArtifactType)
        .OrderBy(g => g.Key)
        .Select(g => new ArtifactTypeCount(g.Key, g.Count(), g.Count(a => a.Origin == ArtifactOrigin.Ai), g.Count(a => a.Origin == ArtifactOrigin.Human)))
        .ToList();

    private static DateTime Minute(DateTime at) => new(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, at.Kind);

    private static string? JoinText(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? (string.IsNullOrWhiteSpace(b) ? null : b) : string.IsNullOrWhiteSpace(b) ? a : $"{a} — {b}";

    private static string Codes(IEnumerable<string> codes)
    {
        var list = codes.OrderBy(c => c).ToList();
        return list.Count <= 6 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(6))} and {list.Count - 6} more";
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBoundary();

    public static string Humanize(string pascal) => WordBoundary().Replace(pascal, " ");

    public static string TypeLabel(ArtifactType type, int count) => (type, count == 1) switch
    {
        (ArtifactType.AcceptanceCriterion, false) => "Acceptance Criteria",
        (ArtifactType.NonFunctionalRequirement, true) => "Non-Functional Requirement",
        (ArtifactType.NonFunctionalRequirement, false) => "Non-Functional Requirements",
        (ArtifactType.ApiSpecification, true) => "API Specification",
        (ArtifactType.ApiSpecification, false) => "API Specifications",
        (ArtifactType.DataEntity, false) => "Data Entities",
        (ArtifactType.DesignArtifact, true) => "Solution Design",
        (ArtifactType.DesignArtifact, false) => "Solution Designs",
        (_, true) => Humanize(type.ToString()),
        _ => Humanize(type.ToString()) + "s"
    };

    // "10 Functional Requirements and 3 Business Rules"
    private static string Describe(IReadOnlyList<ArtifactType> types)
    {
        var parts = types.GroupBy(t => t).OrderBy(g => g.Key).Select(g => $"{g.Count()} {TypeLabel(g.Key, g.Count())}").ToList();
        return parts.Count <= 1 ? parts.FirstOrDefault() ?? "artifacts" : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
