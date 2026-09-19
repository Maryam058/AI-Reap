namespace AiReap.Application.Traceability;

public interface ITraceabilityService
{
    Task<IReadOnlyList<TraceabilityRow>> GetMatrixAsync(Guid projectId, CancellationToken cancellationToken = default);
}
