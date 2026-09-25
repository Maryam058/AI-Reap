using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Notifications;

public record NotificationResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    Guid TransitionId,
    ProjectNotificationKind Kind,
    string Message,
    DateTime CreatedAt,
    DateTime? AcknowledgedAt);

public interface INotificationService
{
    // The caller's own notifications, newest first; open ones only unless includeAcknowledged.
    Task<IReadOnlyList<NotificationResponse>> GetMineAsync(bool includeAcknowledged, CancellationToken cancellationToken = default);

    // Throws KeyNotFoundException when the notification doesn't exist or isn't the caller's
    // (someone else's notice is not revealed). Acknowledging twice is a no-op.
    Task<NotificationResponse> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default);
}

// Read side of the lifecycle notices written by LifecycleNotifications. In-app only.
public class NotificationService : INotificationService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public NotificationService(IAiReapDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<NotificationResponse>> GetMineAsync(bool includeAcknowledged, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        return await (
            from n in _db.ProjectNotifications.AsNoTracking()
            join p in _db.Projects on n.ProjectId equals p.Id
            where n.RecipientUserId == userId && (includeAcknowledged || n.AcknowledgedAt == null)
            orderby n.CreatedAt descending
            select new NotificationResponse(n.Id, n.ProjectId, p.Name, n.TransitionId, n.Kind, n.Message, n.CreatedAt, n.AcknowledgedAt))
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    public async Task<NotificationResponse> AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        var notification = await _db.ProjectNotifications.FirstOrDefaultAsync(n => n.Id == id && n.RecipientUserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Notification {id} not found.");

        if (notification.AcknowledgedAt is null)
        {
            notification.AcknowledgedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var projectName = await _db.Projects.Where(p => p.Id == notification.ProjectId).Select(p => p.Name).FirstAsync(cancellationToken);
        return new NotificationResponse(notification.Id, notification.ProjectId, projectName, notification.TransitionId,
            notification.Kind, notification.Message, notification.CreatedAt, notification.AcknowledgedAt);
    }
}
