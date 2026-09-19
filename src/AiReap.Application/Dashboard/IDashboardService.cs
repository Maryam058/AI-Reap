namespace AiReap.Application.Dashboard;

public interface IDashboardService
{
    Task<ProjectDashboardResponse> GetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
