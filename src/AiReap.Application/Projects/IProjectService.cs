namespace AiReap.Application.Projects;

public interface IProjectService
{
    Task<ProjectResponse> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectResponse>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProjectResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProjectResponse?> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default);

    // Project.Status is changed only by IProjectLifecycleService (Application/Lifecycle).

    Task<IReadOnlyList<StakeholderResponse>> GetStakeholdersAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<StakeholderResponse?> AddStakeholderAsync(Guid projectId, CreateStakeholderRequest request, CancellationToken cancellationToken = default);
    Task<StakeholderResponse?> UpdateStakeholderAsync(Guid projectId, Guid stakeholderId, UpdateStakeholderRequest request, CancellationToken cancellationToken = default);
    Task<bool> RemoveStakeholderAsync(Guid projectId, Guid stakeholderId, CancellationToken cancellationToken = default);

    // §38 DoD — project membership. AddMemberAsync takes an already-resolved userId (the
    // controller resolves AddProjectMemberRequest's email via UserManager first - Application
    // services stay independent of ASP.NET Identity, same reason ICurrentUser is a port).
    Task<IReadOnlyList<ProjectMemberResponse>> GetMembersAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectMemberResponse?> AddMemberAsync(Guid projectId, string userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveMemberAsync(Guid projectId, string userId, CancellationToken cancellationToken = default);
}
