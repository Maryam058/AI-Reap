namespace AiReap.Application.RequirementSources;

public interface IRequirementSourceService
{
    Task<RequirementSourceResponse> CreateAsync(Guid projectId, CreateRequirementSourceRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RequirementSourceResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<RequirementSourceResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
