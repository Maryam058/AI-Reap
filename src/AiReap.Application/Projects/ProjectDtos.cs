using AiReap.Domain.Enums;

namespace AiReap.Application.Projects;

public record CreateProjectRequest(
    string Name,
    string? Description,
    string? BusinessProblem,
    string? Objectives,
    string? Scope,
    string? Domain,
    string? TargetUsers,
    string? TechnologyPreferences,
    string? Constraints,
    string? ExpectedTimeline);

public record UpdateProjectRequest(
    string Name,
    string? Description,
    string? BusinessProblem,
    string? Objectives,
    string? Scope,
    string? Domain,
    string? TargetUsers,
    string? TechnologyPreferences,
    string? Constraints,
    string? ExpectedTimeline);

public record UpdateProjectStatusRequest(ProjectStatus Status);

public record ProjectResponse(
    Guid Id,
    string Name,
    string? Description,
    string? BusinessProblem,
    string? Objectives,
    string? Scope,
    string? Domain,
    string? TargetUsers,
    string? TechnologyPreferences,
    string? Constraints,
    string? ExpectedTimeline,
    ProjectStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// §5/§29 — project stakeholders (REAP-023).
public record CreateStakeholderRequest(string Name, string? RoleInProject, string? ContactInfo);

public record UpdateStakeholderRequest(string Name, string? RoleInProject, string? ContactInfo);

public record StakeholderResponse(Guid Id, Guid ProjectId, string Name, string? RoleInProject, string? ContactInfo);
