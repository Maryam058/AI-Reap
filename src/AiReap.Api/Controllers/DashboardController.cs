using AiReap.Application.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboard;

    public DashboardController(IDashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet("api/projects/{projectId:guid}/dashboard")]
    public async Task<ActionResult<ProjectDashboardResponse>> Get(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _dashboard.GetAsync(projectId, cancellationToken));
    }
}
