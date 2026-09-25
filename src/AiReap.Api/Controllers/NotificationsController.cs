using AiReap.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// In-app lifecycle notifications (handover, approval requested, rejected). Every user sees and
// acknowledges only their own; someone else's notification id answers 404.
[ApiController]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications)
    {
        _notifications = notifications;
    }

    [HttpGet("api/notifications")]
    public async Task<ActionResult<IReadOnlyList<NotificationResponse>>> GetMine([FromQuery] bool includeAcknowledged, CancellationToken cancellationToken) =>
        Ok(await _notifications.GetMineAsync(includeAcknowledged, cancellationToken));

    [HttpPost("api/notifications/{id:guid}/acknowledge")]
    public async Task<ActionResult<NotificationResponse>> Acknowledge(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _notifications.AcknowledgeAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
