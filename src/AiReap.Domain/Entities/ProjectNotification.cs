using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// In-app notice about a lifecycle transition, open until the recipient acknowledges it. Same
// shape as ArtifactImpactNotice. At most one per (transition, recipient, kind), enforced by a
// unique index, so a retry or a double submit never notifies anyone twice.
public class ProjectNotification
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid TransitionId { get; set; }

    public string RecipientUserId { get; set; } = string.Empty;
    public ProjectNotificationKind Kind { get; set; }
    public string Message { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
}
