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

// §38 DoD — project membership (access control, not a stakeholder/contact record). Added by
// email since project membership is a Business Analyst action too, and BAs don't have access
// to the Administrator-only /api/users list to look up a raw user id.
public record ProjectMemberResponse(Guid Id, Guid ProjectId, string UserId, string AddedByUserId, DateTime AddedAt);

public record AddProjectMemberRequest(string Email);
