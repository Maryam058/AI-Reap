using System.Text.Json;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Application.Sdlc;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Lifecycle;

// §5 — the only code that changes Project.Status. Every change is written together with a
// ProjectStageTransition row in the same SaveChanges, so status and history can't disagree.
//
//   Direct stage:  owner (or Administrator) advances -> Completed row, status moves.
//   Gated stage:   owner submits -> Pending row, status unchanged;
//                  a different user with the Reviewer (or Administrator) role approves -> status moves,
//                  or rejects with a reason -> status unchanged; the requester may withdraw.
//   Rollback:      Administrator, reason required, earlier stage only.
//
// Evidence (ILifecycleEvidenceService) is checked on every advance, submission and approval. When
// it fails, the transition is refused with the failing checks, unless an Administrator overrides
// with a reason; the override and the failing checks are both stored on the row.
//
// Concurrency: rows are numbered per project (unique ProjectId+Sequence), so two transitions racing
// from the same state can't both be written; a filtered unique index allows one Pending row per
// project; deciding a pending row is guarded by its rowversion.
public class ProjectLifecycleService : IProjectLifecycleService
{
    public const string SignOffLabel = "QA sign-off recorded";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ILifecycleEvidenceService _evidence;
    private readonly LifecycleNotifications _notifications;

    public ProjectLifecycleService(
        IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        ILifecycleEvidenceService evidence, LifecycleNotifications notifications)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _evidence = evidence;
        _notifications = notifications;
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
        var stages = await _evidence.GetAsync(projectId, cancellationToken);
        var history = await HistoryAsync(projectId, cancellationToken);
        var pending = history.FirstOrDefault(t => t.State == StageTransitionState.Pending);

        var current = stages.Single(s => s.Stage == project.Status);
        var index = ProjectLifecyclePolicy.IndexOf(project.Status);
        var next = ProjectLifecyclePolicy.Next(project.Status);
        var gated = ProjectLifecyclePolicy.IsGated(project.Status);
        var advanceRole = ProjectLifecyclePolicy.AdvanceRole(project.Status, _currentUser.IsInRole);
        var isAdmin = _currentUser.IsInRole(Roles.Administrator);

        var actions = new LifecycleActions(
            CanAdvance: advanceRole is not null && !gated && pending is null,
            CanSubmitForApproval: advanceRole is not null && gated && pending is null,
            CanDecide: pending is not null
                && ProjectLifecyclePolicy.GateDecisionRole(pending.FromStatus!.Value, _currentUser.IsInRole) is not null
                && pending.RequestedByUserId != _currentUser.UserId,
            CanWithdraw: pending is not null && (pending.RequestedByUserId == _currentUser.UserId || isAdmin),
            CanRollback: ProjectLifecyclePolicy.RollbackRole(_currentUser.IsInRole) is not null && index > 0 && pending is null,
            CanOverride: ProjectLifecyclePolicy.OverrideRole(_currentUser.IsInRole) is not null);

        return new ProjectLifecycleState(
            project.Id,
            project.Status,
            ProjectLifecyclePolicy.Stage(project.Status).Label,
            ProjectLifecyclePolicy.OwnerRole(project.Status),
            next,
            gated,
            project.Status == ProjectStatus.Testing,
            current.Checks,
            current.Passed,
            pending is null ? null : ToResponse(pending),
            stages.Where(s => ProjectLifecyclePolicy.IndexOf(s.Stage) < index && !s.Passed).ToList(),
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
                $"Only the {stage.OwnerRole} role (or an Administrator) can move the project out of {stage.Label}.");

        await EnsureNoPendingAsync(projectId, cancellationToken);

        var gated = ProjectLifecyclePolicy.IsGated(from);
        var note = Clean(request.Note);
        var checks = await EvaluateExitAsync(project, role, note, cancellationToken);
        var overrideReason = ResolveOverride(checks, request.OverrideReason, alreadyOverridden: false, stage.Label);

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
            EvidenceSnapshotJson = Snapshot(checks)
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
                $"Only a {ProjectLifecyclePolicy.GateApproverRole} (or an Administrator) can decide moving the project out of {stage.Label}.");

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

            Decide(transition, StageTransitionState.Rejected, role, now, reason);
            _db.ProjectStageReviews.Add(Review(transition, role, ReviewDecision.Rejected, reason, now));
            await _notifications.RejectedAsync(project, transition, cancellationToken);
            await _notifications.CloseApprovalRequestsAsync(transition, now, cancellationToken);
            await SaveTransitionAsync(transition, cancellationToken);
            return ToResponse(transition);
        }

        // Evidence can have changed since submission: judge it again, as it stands now.
        var checks = await EvaluateExitAsync(project, transition.RequestedByRole, transition.RequestNote, cancellationToken);
        var overrideReason = ResolveOverride(checks, request.OverrideReason, transition.IsOverride, stage.Label);
        if (overrideReason is not null)
        {
            transition.IsOverride = true;
            transition.OverrideReason = transition.OverrideReason is null || transition.OverrideReason == overrideReason
                ? overrideReason
                : $"{transition.OverrideReason} | {overrideReason}";
        }

        transition.EvidenceSnapshotJson = Snapshot(checks);
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
    private async Task<IReadOnlyList<SdlcCheck>> EvaluateExitAsync(Project project, string? requesterRole, string? note, CancellationToken cancellationToken)
    {
        var checks = (await _evidence.GetAsync(project.Id, cancellationToken)).Single(s => s.Stage == project.Status).Checks.ToList();
        if (project.Status == ProjectStatus.Testing)
        {
            var signedOff = requesterRole == Roles.QA && !string.IsNullOrWhiteSpace(note);
            checks.Add(new SdlcCheck(SignOffLabel, signedOff, signedOff
                ? "QA submitted Testing for approval with a sign-off note."
                : "Testing must be submitted by QA with a sign-off note."));
        }

        return checks;
    }

    // Returns the override reason to record, or null when the evidence passes. Failing evidence
    // needs an Administrator's reason - unless the request already carries one (a gated request an
    // Administrator submitted with an override, now being approved).
    private string? ResolveOverride(IReadOnlyList<SdlcCheck> checks, string? requestedReason, bool alreadyOverridden, string stageLabel)
    {
        var failing = checks.Where(c => !c.Passed).ToList();
        if (failing.Count == 0)
        {
            return null;
        }

        var reason = Clean(requestedReason);
        if (reason is null)
        {
            if (alreadyOverridden)
            {
                return null;
            }

            throw new LifecycleEvidenceException(
                $"{stageLabel} is not complete: {failing.Count} check(s) failing. An Administrator may override with a reason.",
                failing);
        }

        if (ProjectLifecyclePolicy.OverrideRole(_currentUser.IsInRole) is null)
        {
            throw new UnauthorizedAccessException("Only an Administrator can override failing lifecycle evidence.");
        }

        return reason;
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

    private static string Snapshot(IReadOnlyList<SdlcCheck> checks) => JsonSerializer.Serialize(checks, JsonOptions);

    public static StageTransitionResponse ToResponse(ProjectStageTransition t) => new(
        t.Id, t.ProjectId, t.Sequence, t.Kind, t.State, t.FromStatus, t.ToStatus,
        t.RequestedByUserId, t.RequestedByRole, t.RequestedAt, t.RequestNote,
        t.DecidedByUserId, t.DecidedByRole, t.DecidedAt, t.DecisionReason,
        t.IsOverride, t.OverrideReason,
        JsonSerializer.Deserialize<List<SdlcCheck>>(t.EvidenceSnapshotJson, JsonOptions) ?? [],
        t.Reviews.OrderBy(r => r.ReviewedAt)
            .Select(r => new StageReviewResponse(r.Id, r.ReviewerUserId, r.ReviewerRole, r.Decision, r.Comment, r.ReviewedAt))
            .ToList());
}
