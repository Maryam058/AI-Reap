namespace AiReap.Application.Traceability;

public interface ITraceabilityService
{
    Task<IReadOnlyList<TraceabilityRow>> GetMatrixAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BusinessObjectiveResponse>> GetBusinessObjectivesAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Null when the id is not a functional/non-functional requirement.
    Task<IReadOnlyList<TraceRef>?> SetBusinessObjectivesAsync(Guid requirementId, IReadOnlyList<Guid> objectiveIds, CancellationToken cancellationToken = default);
}
