using AiReap.Api.Authorization;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Projects;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AiReap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly IConflictDetectionService _conflictDetectionService;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProjectsController(IProjectService projectService, IConflictDetectionService conflictDetectionService, UserManager<ApplicationUser> userManager)
    {
        _projectService = projectService;
        _conflictDetectionService = conflictDetectionService;
        _userManager = userManager;
    }

    // §4: project creation is a Business Analyst / Administrator responsibility.
    [HttpPost]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    [Capability("Author", "Create and edit projects and stakeholders", 30)]
    public async Task<ActionResult<ProjectResponse>> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _projectService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { projectId = project.Id }, project);
    }

    [HttpGet]
    [Capability("View", "Projects, requirements, design, tasks, tests, dashboard and traceability", 10)]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _projectService.GetAllAsync(cancellationToken));
    }

    // Route param named projectId (not id) so ProjectMembershipFilter's global route-value
    // check covers this action automatically - see Program.cs / ProjectMembershipFilter.
    [HttpGet("{projectId:guid}")]
    public async Task<ActionResult<ProjectResponse>> GetById(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _projectService.GetByIdAsync(projectId, cancellationToken);
        return project is null ? NotFound() : Ok(project);
    }

    // §5/REAP-020 — edit the project's descriptive fields (not status; see UpdateStatus).
    [HttpPut("{projectId:guid}")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<ActionResult<ProjectResponse>> Update(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await _projectService.UpdateAsync(projectId, request, cancellationToken);
        return project is null ? NotFound() : Ok(project);
    }

    // §24: status transitions represent approval-workflow milestones — Business Analyst
    // drives most of them, Reviewer/Manager drives Approved, Administrator can always act.
    // §5/REAP-021 — only the current stage or the next one in the fixed lifecycle is a legal target.
    [HttpPatch("{projectId:guid}/status")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer}")]
    [Capability("Review", "Move a project to its next SDLC stage", 80)]
    public async Task<ActionResult<ProjectResponse>> UpdateStatus(Guid projectId, UpdateProjectStatusRequest request, CancellationToken cancellationToken)
    {
        var (project, error) = await _projectService.UpdateStatusAsync(projectId, request, cancellationToken);
        if (error is not null)
        {
            return BadRequest(error);
        }

        return project is null ? NotFound() : Ok(project);
    }

    // §15 — scans all functional requirements in the project for duplicates/conflicts.
    // Findings are recorded as ArtifactRelationship rows but always require human resolution.
    [HttpPost("{projectId:guid}/detect-conflicts")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst},{Roles.Reviewer}")]
    [Capability("Review", "Detect conflicts and run requirement quality checks", 90)]
    public async Task<ActionResult<IReadOnlyList<ConflictFinding>>> DetectConflicts(Guid projectId, CancellationToken cancellationToken)
    {
        return Ok(await _conflictDetectionService.DetectAsync(projectId, cancellationToken));
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

    // §38 DoD — project membership (access control). BA/Admin only, mirrors stakeholder-management
    // gating. Added by email, not a raw user id: BusinessAnalyst doesn't have access to the
    // Administrator-only /api/users list to look one up.
    [HttpGet("{projectId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<ProjectMemberResponse>>> GetMembers(Guid projectId, CancellationToken cancellationToken) =>
        Ok(await _projectService.GetMembersAsync(projectId, cancellationToken));

    [HttpPost("{projectId:guid}/members")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    [Capability("Author", "Add or remove project members", 35)]
    public async Task<ActionResult<ProjectMemberResponse>> AddMember(Guid projectId, AddProjectMemberRequest request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return NotFound($"No user with email {request.Email}.");
        }

        var member = await _projectService.AddMemberAsync(projectId, user.Id, cancellationToken);
        return member is null ? NotFound() : CreatedAtAction(nameof(GetMembers), new { projectId }, member);
    }

    [HttpDelete("{projectId:guid}/members/{userId}")]
    [Authorize(Roles = $"{Roles.Administrator},{Roles.BusinessAnalyst}")]
    public async Task<IActionResult> RemoveMember(Guid projectId, string userId, CancellationToken cancellationToken)
    {
        var removed = await _projectService.RemoveMemberAsync(projectId, userId, cancellationToken);
        return removed ? NoContent() : NotFound();
    }
}
