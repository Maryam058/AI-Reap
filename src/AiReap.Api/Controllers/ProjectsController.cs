using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Projects;
using AiReap.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly IConflictDetectionService _conflictDetectionService;

    public ProjectsController(IProjectService projectService, IConflictDetectionService conflictDetectionService)
    {
        _projectService = projectService;
        _conflictDetectionService = conflictDetectionService;
    }

    // §4: project creation is a Business Analyst / Administrator responsibility.
    [HttpPost]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<ActionResult<ProjectResponse>> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _projectService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = project.Id }, project);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _projectService.GetAllAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var project = await _projectService.GetByIdAsync(id, cancellationToken);
        return project is null ? NotFound() : Ok(project);
    }

    // §5/REAP-020 — edit the project's descriptive fields (not status; see UpdateStatus).
    [HttpPut("{id:guid}")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<ActionResult<ProjectResponse>> Update(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _projectService.UpdateAsync(id, request, cancellationToken);
        return project is null ? NotFound() : Ok(project);
    }

    // §24: status transitions represent approval-workflow milestones — Business Analyst
    // drives most of them, Reviewer/Manager drives Approved, Administrator can always act.
    // §5/REAP-021 — only the current stage or the next one in the fixed lifecycle is a legal target.
    [HttpPatch("{id:guid}/status")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer}")]
    public async Task<ActionResult<ProjectResponse>> UpdateStatus(Guid id, UpdateProjectStatusRequest request, CancellationToken cancellationToken)
    {
        var (project, error) = await _projectService.UpdateStatusAsync(id, request, cancellationToken);
        if (error is not null)
        {
            return BadRequest(error);
        }

        return project is null ? NotFound() : Ok(project);
    }

    // §15 — scans all functional requirements in the project for duplicates/conflicts.
    // Findings are recorded as ArtifactRelationship rows but always require human resolution.
    [HttpPost("{id:guid}/detect-conflicts")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer}")]
    public async Task<ActionResult<IReadOnlyList<ConflictFinding>>> DetectConflicts(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _conflictDetectionService.DetectAsync(id, cancellationToken));
    }

    // §5/§29/REAP-023 — project stakeholders.
    [HttpGet("{projectId:guid}/stakeholders")]
    public async Task<ActionResult<IReadOnlyList<StakeholderResponse>>> GetStakeholders(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _projectService.GetStakeholdersAsync(projectId, cancellationToken));
    }

    [HttpPost("{projectId:guid}/stakeholders")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<ActionResult<StakeholderResponse>> AddStakeholder(Guid projectId, CreateStakeholderRequest request, CancellationToken cancellationToken)
    {
        var stakeholder = await _projectService.AddStakeholderAsync(projectId, request, cancellationToken);
        return stakeholder is null ? NotFound() : CreatedAtAction(nameof(GetStakeholders), new { projectId }, stakeholder);
    }

    [HttpPut("{projectId:guid}/stakeholders/{stakeholderId:guid}")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<ActionResult<StakeholderResponse>> UpdateStakeholder(Guid projectId, Guid stakeholderId, UpdateStakeholderRequest request, CancellationToken cancellationToken)
    {
        var stakeholder = await _projectService.UpdateStakeholderAsync(projectId, stakeholderId, request, cancellationToken);
        return stakeholder is null ? NotFound() : Ok(stakeholder);
    }

    [HttpDelete("{projectId:guid}/stakeholders/{stakeholderId:guid}")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<IActionResult> RemoveStakeholder(Guid projectId, Guid stakeholderId, CancellationToken cancellationToken)
    {
        var removed = await _projectService.RemoveStakeholderAsync(projectId, stakeholderId, cancellationToken);
        return removed ? NoContent() : NotFound();
    }
}
