using AiReap.Application.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

// §27 — audit trail is read-only and visible to anyone who can see the project; only
// generation endpoints (gated elsewhere) write to it.
[ApiController]
[Authorize]
public class AuditTrailController : ControllerBase
{
    private readonly IAuditTrailService _auditTrail;

    public AuditTrailController(IAuditTrailService auditTrail)
    {
        _auditTrail = auditTrail;
    }

    [HttpGet("api/projects/{projectId:guid}/ai-executions")]
    public async Task<ActionResult> Get(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _auditTrail.GetForProjectAsync(projectId, cancellationToken));
    }
}
