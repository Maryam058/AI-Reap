using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Lifecycle;

// In-app notices for lifecycle events, added to the same unit of work as the transition that caused
// them, so a transition that fails to save (a lost race, say) notifies nobody. One per
// (transition, recipient, kind): each event happens once per transition, rows already present are
// skipped, and a unique index backs it up.
//
//   Handover           project entered a stage (advance, approval or rollback) -> members holding
//                      the new stage's owner role; Administrators if no member holds it. It hands
//                      over ownership of the new stage, so it goes to every owner - including the
//                      person who made the move, when they hold that role.
//   ApprovalRequested  gated stage submitted -> members holding the Reviewer role (Administrators if
//                      none), other than the requester, who cannot decide their own request anyway
//   TransitionRejected gated request rejected -> the requester
//
// Once a request is approved, rejected or withdrawn there is nothing left to approve, so its
// ApprovalRequested notices are closed (acknowledged) in the same unit of work as the decision.
public class LifecycleNotifications
{
    private readonly IAiReapDbContext _db;
    private readonly IUserRoleDirectory _roles;

    public LifecycleNotifications(IAiReapDbContext db, IUserRoleDirectory roles)
    {
        _db = db;
        _roles = roles;
    }

    public async Task HandoverAsync(Project project, ProjectStageTransition transition, CancellationToken cancellationToken)
    {
        var stage = ProjectLifecyclePolicy.Stage(transition.ToStatus);
        var recipients = await MembersInRoleOrAdministratorsAsync(project.Id, stage.OwnerRole, cancellationToken);
        var message = transition.Kind == StageTransitionKind.Rollback
            ? $"{project.Name} was moved back to {stage.Label}, which your role ({stage.OwnerRole}) owns. Reason: {transition.RequestNote}"
            : $"{project.Name} is now in {stage.Label}, which your role ({stage.OwnerRole}) owns.";
        await AddAsync(project.Id, transition, recipients, ProjectNotificationKind.Handover, message, cancellationToken);
    }

    public async Task ApprovalRequestedAsync(Project project, ProjectStageTransition transition, CancellationToken cancellationToken)
    {
        var from = ProjectLifecyclePolicy.Stage(transition.FromStatus!.Value).Label;
        var to = ProjectLifecyclePolicy.Stage(transition.ToStatus).Label;
        var recipients = await MembersInRoleOrAdministratorsAsync(project.Id, ProjectLifecyclePolicy.GateApproverRole, cancellationToken);
        await AddAsync(project.Id, transition, recipients.Where(id => id != transition.RequestedByUserId), ProjectNotificationKind.ApprovalRequested,
            $"{project.Name}: moving from {from} to {to} is waiting for your approval.", cancellationToken);
    }

    public Task RejectedAsync(Project project, ProjectStageTransition transition, CancellationToken cancellationToken)
    {
        var to = ProjectLifecyclePolicy.Stage(transition.ToStatus).Label;
        return AddAsync(project.Id, transition, [transition.RequestedByUserId], ProjectNotificationKind.TransitionRejected,
            $"{project.Name}: your request to move to {to} was rejected. Reason: {transition.DecisionReason}", cancellationToken);
    }

    // The request is no longer pending: its approval requests stop being actionable for every
    // recipient, whoever decided it. Rows stay (history); only their open state ends.
    public async Task CloseApprovalRequestsAsync(ProjectStageTransition transition, DateTime at, CancellationToken cancellationToken)
    {
        var open = await _db.ProjectNotifications
            .Where(n => n.TransitionId == transition.Id && n.Kind == ProjectNotificationKind.ApprovalRequested && n.AcknowledgedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var notification in open)
        {
            notification.AcknowledgedAt = at;
        }
    }

    private async Task<IReadOnlyList<string>> MembersInRoleOrAdministratorsAsync(Guid projectId, string role, CancellationToken cancellationToken)
    {
        var roles = await ProjectMemberRoles.GetAsync(_db, _roles, projectId, cancellationToken);
        var holders = roles.Where(m => m.Value.Contains(role)).Select(m => m.Key).ToList();
        return holders.Count > 0 ? holders : await _roles.GetActiveUsersInRoleAsync(Roles.Administrator, cancellationToken);
    }

    private async Task AddAsync(Guid projectId, ProjectStageTransition transition, IEnumerable<string> recipients, ProjectNotificationKind kind, string message, CancellationToken cancellationToken)
    {
        var existing = await _db.ProjectNotifications
            .Where(n => n.TransitionId == transition.Id && n.Kind == kind)
            .Select(n => n.RecipientUserId)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var recipient in recipients.Distinct().Except(existing))
        {
            _db.ProjectNotifications.Add(new ProjectNotification
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                TransitionId = transition.Id,
                RecipientUserId = recipient,
                Kind = kind,
                Message = message.Length > 500 ? message[..497] + "..." : message,
                CreatedAt = now
            });
        }
    }
}
