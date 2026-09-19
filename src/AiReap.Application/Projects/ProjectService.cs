using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Projects;

public class ProjectService : IProjectService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ProjectService(IAiReapDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ProjectResponse> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            BusinessProblem = request.BusinessProblem,
            Objectives = request.Objectives,
            Scope = request.Scope,
            Domain = request.Domain,
            TargetUsers = request.TargetUsers,
            TechnologyPreferences = request.TechnologyPreferences,
            Constraints = request.Constraints,
            ExpectedTimeline = request.ExpectedTimeline,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken);

        return ToResponse(project);
    }

    public async Task<IReadOnlyList<ProjectResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _db.Projects
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        return projects.Select(ToResponse).ToList();
    }

    public async Task<ProjectResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return project is null ? null : ToResponse(project);
    }

    public async Task<ProjectResponse?> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return null;
        }

        project.Name = request.Name;
        project.Description = request.Description;
        project.BusinessProblem = request.BusinessProblem;
        project.Objectives = request.Objectives;
        project.Scope = request.Scope;
        project.Domain = request.Domain;
        project.TargetUsers = request.TargetUsers;
        project.TechnologyPreferences = request.TechnologyPreferences;
        project.Constraints = request.Constraints;
        project.ExpectedTimeline = request.ExpectedTimeline;
        project.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return ToResponse(project);
    }

    // §5/REAP-021: the lifecycle is the fixed linear sequence the enum declares
    // (Draft -> ... -> Completed). Only staying put or advancing exactly one stage is legal -
    // no skipping ahead, no moving backward. There's no "reopen a completed project" or
    // "reject back to draft" concept in the spec, so none is invented here.
    public async Task<(ProjectResponse? Project, string? Error)> UpdateStatusAsync(Guid id, UpdateProjectStatusRequest request, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return (null, null);
        }

        var current = (int)project.Status;
        var target = (int)request.Status;

        if (target != current && target != current + 1)
        {
            return (null, $"Invalid status transition: {project.Status} can only move to itself or to the next stage in the lifecycle.");
        }

        project.Status = request.Status;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return (ToResponse(project), null);
    }

    public async Task<IReadOnlyList<StakeholderResponse>> GetStakeholdersAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await _db.ProjectStakeholders
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Name)
            .Select(s => new StakeholderResponse(s.Id, s.ProjectId, s.Name, s.RoleInProject, s.ContactInfo))
            .ToListAsync(cancellationToken);
    }

    public async Task<StakeholderResponse?> AddStakeholderAsync(Guid projectId, CreateStakeholderRequest request, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var stakeholder = new ProjectStakeholder
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = request.Name,
            RoleInProject = request.RoleInProject,
            ContactInfo = request.ContactInfo
        };

        _db.ProjectStakeholders.Add(stakeholder);
        await _db.SaveChangesAsync(cancellationToken);

        return new StakeholderResponse(stakeholder.Id, stakeholder.ProjectId, stakeholder.Name, stakeholder.RoleInProject, stakeholder.ContactInfo);
    }

    public async Task<StakeholderResponse?> UpdateStakeholderAsync(Guid projectId, Guid stakeholderId, UpdateStakeholderRequest request, CancellationToken cancellationToken = default)
    {
        var stakeholder = await _db.ProjectStakeholders
            .FirstOrDefaultAsync(s => s.Id == stakeholderId && s.ProjectId == projectId, cancellationToken);
        if (stakeholder is null)
        {
            return null;
        }

        stakeholder.Name = request.Name;
        stakeholder.RoleInProject = request.RoleInProject;
        stakeholder.ContactInfo = request.ContactInfo;

        await _db.SaveChangesAsync(cancellationToken);

        return new StakeholderResponse(stakeholder.Id, stakeholder.ProjectId, stakeholder.Name, stakeholder.RoleInProject, stakeholder.ContactInfo);
    }

    public async Task<bool> RemoveStakeholderAsync(Guid projectId, Guid stakeholderId, CancellationToken cancellationToken = default)
    {
        var stakeholder = await _db.ProjectStakeholders
            .FirstOrDefaultAsync(s => s.Id == stakeholderId && s.ProjectId == projectId, cancellationToken);
        if (stakeholder is null)
        {
            return false;
        }

        _db.ProjectStakeholders.Remove(stakeholder);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ProjectResponse ToResponse(Project project) => new(
        project.Id,
        project.Name,
        project.Description,
        project.BusinessProblem,
        project.Objectives,
        project.Scope,
        project.Domain,
        project.TargetUsers,
        project.TechnologyPreferences,
        project.Constraints,
        project.ExpectedTimeline,
        project.Status,
        project.CreatedAt,
        project.UpdatedAt);
}
