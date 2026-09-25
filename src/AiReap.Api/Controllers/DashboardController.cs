using AiReap.Application.Dashboard;
using AiReap.Application.Projects;
using AiReap.Application.Sdlc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboard;
    private readonly IProjectService _projects;
    private readonly ISdlcProgressService _sdlcProgress;

    public DashboardController(IDashboardService dashboard, IProjectService projects, ISdlcProgressService sdlcProgress)
    {
        _dashboard = dashboard;
        _projects = projects;
        _sdlcProgress = sdlcProgress;
    }

    [HttpGet("api/projects/{projectId:guid}/dashboard")]
    public async Task<ActionResult<ProjectDashboardResponse>> Get(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _dashboard.GetAsync(projectId, cancellationToken));
    }

    // §28 — evidence-based SDLC progress for every project the caller can see, so list views
    // match each project's own dashboard. Scoped through the same membership filter as GET /api/projects.
    [HttpGet("api/projects/sdlc-progress")]
    public async Task<ActionResult<IReadOnlyList<ProjectSdlcProgress>>> GetSdlcProgress(CancellationToken cancellationToken)
    {
        var visible = await _projects.GetAllAsync(cancellationToken);
        return Ok(await _sdlcProgress.GetManyAsync(visible.Select(p => p.Id).ToList(), cancellationToken));
    }
}
