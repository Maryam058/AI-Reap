namespace AiReap.Application.Audit;

public interface IAuditTrailService
{
    Task<IReadOnlyList<AiExecutionResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}
