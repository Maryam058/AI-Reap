namespace AiReap.Application.Projects;

public interface IProjectService
{
    Task<ProjectResponse> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectResponse>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProjectResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProjectResponse?> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default);

    // Returns null if the project doesn't exist, or a validation message if the requested
    // status isn't a legal transition from the project's current status (REAP-021).
    Task<(ProjectResponse? Project, string? Error)> UpdateStatusAsync(Guid id, UpdateProjectStatusRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StakeholderResponse>> GetStakeholdersAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<StakeholderResponse?> AddStakeholderAsync(Guid projectId, CreateStakeholderRequest request, CancellationToken cancellationToken = default);
    Task<StakeholderResponse?> UpdateStakeholderAsync(Guid projectId, Guid stakeholderId, UpdateStakeholderRequest request, CancellationToken cancellationToken = default);
    Task<bool> RemoveStakeholderAsync(Guid projectId, Guid stakeholderId, CancellationToken cancellationToken = default);
}
