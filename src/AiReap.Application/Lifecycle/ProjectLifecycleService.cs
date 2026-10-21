using System.Text.Json;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Application.Sdlc;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Lifecycle;

// §5 — the only code that changes Project.Status. Every change is written together with a
// ProjectStageTransition row in the same SaveChanges, so status and history can't disagree.
//
//   Direct stage:  owner advances -> Completed row, status moves.
//   Gated stage:   owner submits -> Pending row, status unchanged;
//                  a different user with the Reviewer role approves -> status moves,
//                  or rejects with a reason -> status unchanged; the requester may withdraw.
//   Rollback:      Administrator, reason required, earlier stage only.
//
// Evidence (ILifecycleEvidenceService) is checked on every advance, submission and approval. When
// it fails, the transition is refused with the failing checks, unless an Administrator overrides
// with a reason; the override and the failing checks are both stored on the row.
//
// R2 - an Administrator who does not hold the owner's (or, approving a gate, the Reviewer's) role
// may still move the stage, but only as an override with a reason (StandInOverride): Administrator
// privileges never stand in for the owner silently. Rejecting a gate already needs a reason and
// moves nothing, so an Administrator may reject without an override.
//
// Concurrency: rows are numbered per project (unique ProjectId+Sequence), so two transitions racing
// from the same state can't both be written; a filtered unique index allows one Pending row per
// project; deciding a pending row is guarded by its rowversion.
//
// Evidence is judged as observed during evaluation. The artifacts it reads are not locked or
// re-checked when the transition is saved, so an artifact may still change between the evaluation
// and that save; the gate does not claim otherwise. What the row records (EvidenceSnapshotJson,
// DecisionEvidenceSnapshotJson) is the evidence that was actually evaluated.
public class ProjectLifecycleService : IProjectLifecycleService
{
    public const string SignOffLabel = "QA sign-off recorded";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILifecycleEvidenceService _evidence;
    private readonly LifecycleNotifications _notifications;
    private readonly ExitCheckGuidanceService _guidance;
    private readonly ILogger<ProjectLifecycleService> _logger;

    public ProjectLifecycleService(
        IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILifecycleEvidenceService evidence, LifecycleNotifications notifications, ExitCheckGuidanceService guidance,
        ILogger<ProjectLifecycleService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _evidence = evidence;
        _notifications = notifications;
        _guidance = guidance;
        _logger = logger;
    }

    // The first history row of a new project (ProjectService.CreateAsync), matching the Baseline
    // rows the ProjectLifecycle migration backfilled for existing projects.
    public static ProjectStageTransition NewBaseline(Guid projectId, ProjectStatus status, string userId, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Sequence = 0,
        Kind = StageTransitionKind.Baseline,
        State = StageTransitionState.Completed,
        FromStatus = null,
        ToStatus = status,
        RequestedByUserId = userId,
        RequestedAt = at,
        DecidedByUserId = userId,
        DecidedAt = at,
        RequestNote = "Project created."
    };

    public async Task<ProjectLifecycleState> GetStateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectAsync(projectId, cancellationToken);
        var stages = await _evidence.GetDetailedAsync(projectId, cancellationToken);
        var view = await _guidance.ForProjectAsync(projectId, cancellationToken);
        var history = await HistoryAsync(projectId, cancellationToken);
        var pending = history.FirstOrDefault(t => t.State == StageTransitionState.Pending);

        var current = stages.Single(s => s.Stage == project.Status);
        var index = ProjectLifecyclePolicy.IndexOf(project.Status);
        var next = ProjectLifecyclePolicy.Next(project.Status);
        var gated = ProjectLifecyclePolicy.IsGated(project.Status);
        var advanceRole = ProjectLifecyclePolicy.AdvanceRole(project.Status, _currentUser.IsInRole);
        var decideRole = pending is null ? null : ProjectLifecyclePolicy.GateDecisionRole(pending.FromStatus!.Value, _currentUser.IsInRole);
        var isAdmin = _currentUser.IsInRole(Roles.Administrator);

        var actions = new LifecycleActions(
            CanAdvance: advanceRole is not null && !gated && pending is null,
            CanSubmitForApproval: advanceRole is not null && gated && pending is null,
            CanDecide: decideRole is not null && pending!.RequestedByUserId != _currentUser.UserId,
            CanWithdraw: pending is not null && (pending.RequestedByUserId == _currentUser.UserId || isAdmin),
            CanRollback: ProjectLifecyclePolicy.RollbackRole(_currentUser.IsInRole) is not null && index > 0 && pending is null,
            CanOverride: ProjectLifecyclePolicy.OverrideRole(_currentUser.IsInRole) is not null,
            AdvanceIsOverride: advanceRole == Roles.Administrator,
            DecideIsOverride: decideRole == Roles.Administrator);

        return new ProjectLifecycleState(
            project.Id,
            project.Status,
            ProjectLifecyclePolicy.Stage(project.Status).Label,
            ProjectLifecyclePolicy.OwnerRole(project.Status),
            next,
            gated,
            project.Status == ProjectStatus.Testing,
            current.Checks.Select(view).ToList(),
            current.Checks.All(c => c.Check.Passed),
            pending is null ? null : ToResponse(pending),
            stages
                .Where(s => ProjectLifecyclePolicy.IndexOf(s.Stage) < index && !s.Checks.All(c => c.Check.Passed))
                .Select(s => new StageEvidenceView(s.Stage, s.Label, s.OwnerRole, s.Checks.Select(view).ToList()))
                .ToList(),
            actions,
            history.Select(ToResponse).ToList());
    }

    public async Task<IReadOnlyList<StageTransitionResponse>> GetHistoryAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await LoadProjectAsync(projectId, cancellationToken);
        return (await HistoryAsync(projectId, cancellationToken)).Select(ToResponse).ToList();
    }

    // Comment-only review of a pending request: feedback that neither approves nor rejects it.
    // Any project member may comment; the comment is recorded under the role they acted as.
    public async Task<StageReviewResponse> CommentAsync(Guid transitionId, string? comment, CancellationToken cancellationToken = default)
    {
        var transition = await LoadTransitionAsync(transitionId, cancellationToken);
        await LoadProjectAsync(transition.ProjectId, cancellationToken);
        EnsurePending(transition);

        var text = Clean(comment) ?? throw new RequestValidationException("A comment is required.");
        var role = new[] { Roles.Reviewer, Roles.Developer, Roles.QA, Roles.BusinessAnalyst, Roles.Administrator }
            .FirstOrDefault(_currentUser.IsInRole);
        var review = Review(transition, role ?? string.Empty, ReviewDecision.CommentOnly, text, DateTime.UtcNow);
        review.ReviewerRole = role;
        _db.ProjectStageReviews.Add(review);
        await _db.SaveChangesAsync(cancellationToken);
        return new StageReviewResponse(review.Id, review.ReviewerUserId, review.ReviewerRole, review.Decision, review.Comment, review.ReviewedAt);
    }

    private Task<List<ProjectStageTransition>> HistoryAsync(Guid projectId, CancellationToken cancellationToken) =>
        _db.ProjectStageTransitions
            .AsNoTracking()
            .Include(t => t.Reviews)
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.Sequence)
            .ToListAsync(cancellationToken);

    public async Task<StageTransitionResponse> AdvanceAsync(Guid projectId, AdvanceStageRequest request, CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectAsync(projectId, cancellationToken);
        var from = project.Status;
        var stage = ProjectLifecyclePolicy.Stage(from);

        if (ProjectLifecyclePolicy.IsFinal(from))
        {
            throw new ConflictException($"The project is {stage.Label}; there is no later stage.");
        }

        var next = ProjectLifecyclePolicy.Next(from)!.Value;
        if (!ProjectLifecyclePolicy.IsLegalAdvance(from, request.TargetStatus))
        {
            throw new ConflictException(
                $"A project in {stage.Label} can only move to {ProjectLifecyclePolicy.Stage(next).Label}; stages cannot be skipped.");
        }

        var role = ProjectLifecyclePolicy.AdvanceRole(from, _currentUser.IsInRole)
            ?? throw new UnauthorizedAccessException(
                $"Only the {stage.OwnerRole} role (or an Administrator, as an override) can move the project out of {stage.Label}.");

        await EnsureNoPendingAsync(projectId, cancellationToken);

        var gated = ProjectLifecyclePolicy.IsGated(from);
        var note = Clean(request.Note);
        var checks = await EvaluateExitAsync(project, role, note, cancellationToken);
        var overrideReason = await ResolveOverrideAsync(projectId, checks, request.OverrideReason, NoAuthorizedFailures, stage.Label, cancellationToken)
            ?? StandInOverride(role, stage.OwnerRole, request.OverrideReason, $"move the project out of {stage.Label}");

        var now = DateTime.UtcNow;
        var transition = new ProjectStageTransition
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Sequence = await NextSequenceAsync(projectId, cancellationToken),
            Kind = StageTransitionKind.Advance,
            State = gated ? StageTransitionState.Pending : StageTransitionState.Completed,
            FromStatus = from,
            ToStatus = request.TargetStatus,
            RequestedByUserId = _currentUser.UserId,
            RequestedByRole = role,
            RequestedAt = now,
            RequestNote = note,
            IsOverride = overrideReason is not null,
            OverrideReason = overrideReason,
            EvidenceSnapshotJson = OverrideScope.Snapshot(checks)
        };

        if (!gated)
        {
            transition.DecidedByUserId = _currentUser.UserId;
            transition.DecidedByRole = role;
            transition.DecidedAt = now;
            project.Status = request.TargetStatus;
            project.UpdatedAt = now;
        }

        _db.ProjectStageTransitions.Add(transition);
        if (gated)
        {
            await _notifications.ApprovalRequestedAsync(project, transition, cancellationToken);
        }
        else
        {
            await _notifications.HandoverAsync(project, transition, cancellationToken);
        }

        await SaveTransitionAsync(transition, cancellationToken);
        return ToResponse(transition);
    }

    public async Task<StageTransitionResponse> DecideAsync(Guid transitionId, DecideStageTransitionRequest request, CancellationToken cancellationToken = default)
    {
        var transition = await LoadTransitionAsync(transitionId, cancellationToken);
        var project = await LoadProjectAsync(transition.ProjectId, cancellationToken);
        EnsurePending(transition);

        var from = transition.FromStatus!.Value;
        var stage = ProjectLifecyclePolicy.Stage(from);
        var role = ProjectLifecyclePolicy.GateDecisionRole(from, _currentUser.IsInRole)
            ?? throw new UnauthorizedAccessException(
                $"Only a {ProjectLifecyclePolicy.GateApproverRole} (or an Administrator, as an override) can decide moving the project out of {stage.Label}.");

        if (transition.RequestedByUserId == _currentUser.UserId)
        {
            throw new UnauthorizedAccessException("The person who submitted a stage for approval cannot also decide it.");
        }

        if (project.Status != from)
        {
            throw new ConflictException("The project is no longer in the stage this request was made from.");
        }

        var now = DateTime.UtcNow;
        var reason = Clean(request.Reason);

        if (!request.Approve)
        {
            if (reason is null)
            {
                throw new RequestValidationException("A reason is required to reject a stage transition.");
            }

            // D3 - the evidence as it stands at rejection, for the record only: it never blocks a rejection.
            transition.DecisionEvidenceSnapshotJson = await RejectionEvidenceSnapshotAsync(project, transition, cancellationToken);
            Decide(transition, StageTransitionState.Rejected, role, now, reason);
            _db.ProjectStageReviews.Add(Review(transition, role, ReviewDecision.Rejected, reason, now));
            await _notifications.RejectedAsync(project, transition, cancellationToken);
            await _notifications.CloseApprovalRequestsAsync(transition, now, cancellationToken);
            await SaveTransitionAsync(transition, cancellationToken);
            return ToResponse(transition);
        }

        // Evidence can have changed since submission: judge it again, as it stands now. An override
        // given at submission covers only the failures it was given over (F3).
        var checks = await EvaluateExitAsync(project, transition.RequestedByRole, transition.RequestNote, cancellationToken);
        var overrideReason = await ResolveOverrideAsync(project.Id, checks, request.OverrideReason, AuthorizedFailures(transition), stage.Label, cancellationToken)
            ?? StandInOverride(role, ProjectLifecyclePolicy.GateApproverRole, request.OverrideReason, $"approve moving the project out of {stage.Label}");
        if (overrideReason is not null)
        {
            transition.IsOverride = true;
            transition.OverrideReason = transition.OverrideReason is null || transition.OverrideReason == overrideReason
                ? overrideReason
                : $"{transition.OverrideReason} | {overrideReason}";
        }

        // D3 - the submission snapshot (EvidenceSnapshotJson) stays as submitted; the approval's own
        // evaluation is recorded beside it.
        transition.DecisionEvidenceSnapshotJson = OverrideScope.Snapshot(checks);
        Decide(transition, StageTransitionState.Completed, role, now, reason);
        // Added through the DbSet: a pre-keyed child added via the navigation would be treated as existing.
        _db.ProjectStageReviews.Add(Review(transition, role, ReviewDecision.Approved, reason, now));
        project.Status = transition.ToStatus;
        project.UpdatedAt = now;
        await _notifications.HandoverAsync(project, transition, cancellationToken);
        await _notifications.CloseApprovalRequestsAsync(transition, now, cancellationToken);

        await SaveTransitionAsync(transition, cancellationToken);
        return ToResponse(transition);
    }

    public async Task<StageTransitionResponse> WithdrawAsync(Guid transitionId, string? note, CancellationToken cancellationToken = default)
    {
        var transition = await LoadTransitionAsync(transitionId, cancellationToken);
        await LoadProjectAsync(transition.ProjectId, cancellationToken);
        EnsurePending(transition);

        var isAdmin = _currentUser.IsInRole(Roles.Administrator);
        if (transition.RequestedByUserId != _currentUser.UserId && !isAdmin)
        {
            throw new UnauthorizedAccessException("Only the person who submitted this request (or an Administrator) can withdraw it.");
        }

        var role = transition.RequestedByUserId == _currentUser.UserId ? transition.RequestedByRole : Roles.Administrator;
        var now = DateTime.UtcNow;
        Decide(transition, StageTransitionState.Withdrawn, role, now, Clean(note));
        await _notifications.CloseApprovalRequestsAsync(transition, now, cancellationToken);
        await SaveTransitionAsync(transition, cancellationToken);
        return ToResponse(transition);
    }

    public async Task<StageTransitionResponse> RollbackAsync(Guid projectId, RollbackStageRequest request, CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectAsync(projectId, cancellationToken);
        var role = ProjectLifecyclePolicy.RollbackRole(_currentUser.IsInRole)
            ?? throw new UnauthorizedAccessException("Only an Administrator can move a project back to an earlier stage.");

        var reason = Clean(request.Reason)
            ?? throw new RequestValidationException("A reason is required to roll a project back.");

        if (!ProjectLifecyclePolicy.IsLegalRollback(project.Status, request.TargetStatus))
        {
            throw new ConflictException(
                $"A rollback must target a stage earlier than {ProjectLifecyclePolicy.Stage(project.Status).Label}.");
        }

        await EnsureNoPendingAsync(projectId, cancellationToken);

        // Snapshot of the stage being left, so the history shows what state it was in.
        var checks = (await _evidence.GetAsync(projectId, cancellationToken)).Single(s => s.Stage == project.Status).Checks;
        var now = DateTime.UtcNow;
        var transition = new ProjectStageTransition
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Sequence = await NextSequenceAsync(projectId, cancellationToken),
            Kind = StageTransitionKind.Rollback,
            State = StageTransitionState.Completed,
            FromStatus = project.Status,
            ToStatus = request.TargetStatus,
            RequestedByUserId = _currentUser.UserId,
            RequestedByRole = role,
            RequestedAt = now,
            RequestNote = reason,
            DecidedByUserId = _currentUser.UserId,
            DecidedByRole = role,
            DecidedAt = now,
            EvidenceSnapshotJson = Snapshot(checks)
        };

        project.Status = request.TargetStatus;
        project.UpdatedAt = now;
        _db.ProjectStageTransitions.Add(transition);
        await _notifications.HandoverAsync(project, transition, cancellationToken);
        await SaveTransitionAsync(transition, cancellationToken);
        return ToResponse(transition);
    }

    // The current stage's exit checks, plus - for the Testing gate - QA's sign-off, which is the
    // submission itself: made under the QA role, with a sign-off note.
    private async Task<IReadOnlyList<CheckEvidence>> EvaluateExitAsync(Project project, string? requesterRole, string? note, CancellationToken cancellationToken)
    {
        var checks = (await _evidence.GetDetailedAsync(project.Id, cancellationToken)).Single(s => s.Stage == project.Status).Checks.ToList();
        if (project.Status == ProjectStatus.Testing)
        {
            var signedOff = requesterRole == Roles.QA && !string.IsNullOrWhiteSpace(note);
            checks.Add(new CheckEvidence(
                new SdlcCheck(SignOffLabel, signedOff, signedOff
                    ? "QA submitted Testing for approval with a sign-off note."
                    : "Testing must be submitted by QA with a sign-off note."),
                ExitCheckKeys.QaSignOff,
                signedOff ? [] : [CheckBlocker.Of(BlockerReasons.SignOff)]));
        }

        return checks;
    }

    // D3 - the same evaluation an approval makes, recorded on a rejection. A rejection never depends on
    // evidence, so a failure to evaluate it (anything but cancellation) does not stop the rejection:
    // the decision snapshot is then left null ("not recorded"), never a made-up or partial one. The
    // failure is logged with identifiers only - no evidence text, quotes or requirement content.
    private async Task<string?> RejectionEvidenceSnapshotAsync(Project project, ProjectStageTransition transition, CancellationToken cancellationToken)
    {
        try
        {
            return OverrideScope.Snapshot(await EvaluateExitAsync(project, transition.RequestedByRole, transition.RequestNote, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Stage exit evidence could not be evaluated while rejecting stage transition {TransitionId} of project {ProjectId} ({FromStatus} -> {ToStatus}); the rejection is recorded without a decision evidence snapshot.",
                transition.Id, project.Id, transition.FromStatus, transition.ToStatus);
            return null;
        }
    }

    private static readonly IReadOnlySet<string> NoAuthorizedFailures = new HashSet<string>();

    // F3 - the failures an Administrator's override at submission was given over: the failure
    // identities (OverrideScope) of the submission snapshot of a request flagged as an override. A
    // request is flagged when ResolveOverrideAsync accepted an Administrator's reason over failing
    // checks, or when it was only a stand-in (StandInOverride); the second kind is submitted with
    // every check passing, so its snapshot has no failures and it covers none. A failure is covered
    // only while it is the same failure (same check, reason and artifacts), whatever its label or
    // detail says: a check that newly fails, or fails over different artifacts, needs a fresh override.
    private static IReadOnlySet<string> AuthorizedFailures(ProjectStageTransition transition) =>
        transition.IsOverride ? OverrideScope.Authorized(transition.EvidenceSnapshotJson) : NoAuthorizedFailures;

    // Returns the override reason to record, or null when the evidence passes. Failing evidence
    // needs an Administrator's reason - unless every failing check is one the request's own override
    // already covers (AuthorizedFailures: a gated request an Administrator submitted with an override
    // over exactly these failures, now being approved).
    private async Task<string?> ResolveOverrideAsync(
        Guid projectId, IReadOnlyList<CheckEvidence> checks, string? requestedReason,
        IReadOnlySet<string> authorizedFailures, string stageLabel, CancellationToken cancellationToken)
    {
        var failing = checks.Where(c => !c.Check.Passed).ToList();
        if (failing.Count == 0)
        {
            return null;
        }

        var reason = Clean(requestedReason);
        if (reason is null)
        {
            var uncovered = failing.Where(c => !OverrideScope.Covers(authorizedFailures, c)).ToList();
            if (uncovered.Count == 0)
            {
                return null;
            }

            throw new LifecycleEvidenceException(
                authorizedFailures.Count == 0
                    ? $"{stageLabel} is not complete: {failing.Count} check(s) failing. An Administrator may override with a reason."
                    : $"{stageLabel} is not complete: {uncovered.Count} check(s) failing that the override given at submission did not cover (new or changed since). An Administrator may override with a reason.",
                await _guidance.ViewsAsync(projectId, uncovered, cancellationToken));
        }

        if (ProjectLifecyclePolicy.OverrideRole(_currentUser.IsInRole) is null)
        {
            throw new UnauthorizedAccessException("Only an Administrator can override failing lifecycle evidence.");
        }

        return reason;
    }

    // R2 - Administrator is a system role, not a stand-in for the stage owner or the Reviewer. When the
    // caller acts only as an Administrator (they do not hold the role the duty belongs to), the move is
    // allowed solely as an explicit override: a reason is required and the row is flagged as one.
    // Returns that reason, or null when the caller holds the duty's own role.
    private string? StandInOverride(string actingRole, string dutyRole, string? requestedReason, string action)
    {
        if (actingRole != Roles.Administrator)
        {
            return null;
        }

        return Clean(requestedReason) ?? throw new UnauthorizedAccessException(
            $"An Administrator is not the {dutyRole} here. To {action} in the {dutyRole}'s place, give an override reason; it is recorded as an override.");
    }

    private void Decide(ProjectStageTransition transition, StageTransitionState state, string? role, DateTime at, string? reason)
    {
        transition.State = state;
        transition.DecidedByUserId = _currentUser.UserId;
        transition.DecidedByRole = role;
        transition.DecidedAt = at;
        transition.DecisionReason = reason;
    }

    private ProjectStageReview Review(ProjectStageTransition transition, string role, ReviewDecision decision, string? comment, DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        TransitionId = transition.Id,
        ReviewerUserId = _currentUser.UserId,
        ReviewerRole = role,
        Decision = decision,
        Comment = comment,
        ReviewedAt = at
    };

    private async Task<Project> LoadProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project {projectId} not found.");
        await _projectAccess.EnsureMemberAsync(projectId, cancellationToken);
        return project;
    }

    private async Task<ProjectStageTransition> LoadTransitionAsync(Guid transitionId, CancellationToken cancellationToken) =>
        await _db.ProjectStageTransitions.Include(t => t.Reviews).FirstOrDefaultAsync(t => t.Id == transitionId, cancellationToken)
        ?? throw new KeyNotFoundException($"Stage transition {transitionId} not found.");

    private static void EnsurePending(ProjectStageTransition transition)
    {
        if (transition.State != StageTransitionState.Pending)
        {
            throw new ConflictException($"This request is already {transition.State.ToString().ToLowerInvariant()}.");
        }
    }

    private async Task EnsureNoPendingAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (await _db.ProjectStageTransitions.AnyAsync(t => t.ProjectId == projectId && t.State == StageTransitionState.Pending, cancellationToken))
        {
            throw new ConflictException("A stage transition is already waiting for approval. Decide or withdraw it first.");
        }
    }

    private async Task<int> NextSequenceAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await _db.ProjectStageTransitions.Where(t => t.ProjectId == projectId).MaxAsync(t => (int?)t.Sequence, cancellationToken) ?? -1) + 1;

    // A lost race surfaces as a unique-index violation (same Sequence, or a second Pending row) or a
    // rowversion mismatch on a decided row. Either way nothing was written; the caller reloads.
    private async Task SaveTransitionAsync(ProjectStageTransition transition, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            if (_db is DbContext context)
            {
                foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
                {
                    entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
                }
            }

            throw new ConflictException(ex is DbUpdateConcurrencyException
                ? "This request was just acted on by someone else. Reload to see its current state."
                : "The project's stage was just changed by someone else. Reload to see its current state.");
        }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // Plain SdlcCheck only: history is shown to every member, so per-viewer guidance is never stored.
    private static string Snapshot(IReadOnlyList<SdlcCheck> checks) => JsonSerializer.Serialize(checks, JsonOptions);

    public static StageTransitionResponse ToResponse(ProjectStageTransition t) => new(
        t.Id, t.ProjectId, t.Sequence, t.Kind, t.State, t.FromStatus, t.ToStatus,
        t.RequestedByUserId, t.RequestedByRole, t.RequestedAt, t.RequestNote,
        t.DecidedByUserId, t.DecidedByRole, t.DecidedAt, t.DecisionReason,
        t.IsOverride, t.OverrideReason,
        PublicChecks(t.EvidenceSnapshotJson) ?? [],
        t.Reviews.OrderBy(r => r.ReviewedAt)
            .Select(r => new StageReviewResponse(r.Id, r.ReviewerUserId, r.ReviewerRole, r.Decision, r.Comment, r.ReviewedAt))
            .ToList(),
        t.DecisionEvidenceSnapshotJson is null ? null : PublicChecks(t.DecisionEvidenceSnapshotJson) ?? [],
        SubmissionEvidenceUnavailable(t));

    // D3 - true only when this row's EvidenceSnapshotJson is known NOT to be its submission evidence:
    // an approved gated request (Advance from a gated stage, Completed, decided by someone other than
    // the requester) that has no decision snapshot. Every approval this code makes records a decision
    // snapshot and leaves the submission one as submitted; the approval code before D3 instead
    // replaced the submission snapshot with the approval-time checks and recorded no decision
    // snapshot. So such a row's single snapshot is approval-time evidence and its submission evidence
    // is not available. This is an availability flag only: it does not claim when or why the row was
    // written that way, and a null decision snapshot alone (a rejection whose re-evaluation failed, a
    // pending, withdrawn or direct row) never sets it - those rows still hold their submission snapshot.
    public static bool SubmissionEvidenceUnavailable(ProjectStageTransition t) =>
        t.Kind == StageTransitionKind.Advance
        && t.State == StageTransitionState.Completed
        && t.FromStatus is { } from && ProjectLifecyclePolicy.IsGated(from)
        && t.DecidedByUserId is not null && t.DecidedByUserId != t.RequestedByUserId
        && t.DecisionEvidenceSnapshotJson is null;

    // Read as plain SdlcCheck (label, passed, detail): the snapshot's check keys and failure identities
    // (OverrideScope) never leave the server.
    private static List<SdlcCheck>? PublicChecks(string snapshotJson) => JsonSerializer.Deserialize<List<SdlcCheck>>(snapshotJson, JsonOptions);
}
