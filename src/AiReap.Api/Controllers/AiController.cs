using AiReap.Application.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class AiController : ControllerBase
{
    private readonly IAiHealthCheckService _healthCheck;

    public AiController(IAiHealthCheckService healthCheck)
    {
        _healthCheck = healthCheck;
    }

    [HttpGet("api/ai/status")]
    public async Task<ActionResult<AiProviderStatus>> GetStatus(CancellationToken cancellationToken)
    {
        return Ok(await _healthCheck.CheckAsync(cancellationToken));
    }

    // Token usage reported by the provider since this API instance started (in memory; see AiUsageMeter).
    [HttpGet("api/ai/usage")]
    [Authorize(Roles = AiReap.Domain.Common.Roles.Administrator)]
    public ActionResult<AiUsageSnapshot> GetUsage([FromServices] IAiUsageMeter usage) => Ok(usage.Snapshot());
}
