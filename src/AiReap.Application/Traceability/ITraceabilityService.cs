namespace AiReap.Application.Traceability;

public interface ITraceabilityService
{
    Task<IReadOnlyList<TraceabilityRow>> GetMatrixAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<TraceabilityGapsResponse> GetGapsAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Links a downstream artifact to the requirement (or, for an acceptance criterion, the story) it
    // traces to, with the relationship its type uses. Null when the artifact is not found.
    Task<TraceRef?> LinkToRequirementAsync(Guid artifactId, Guid targetArtifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BusinessObjectiveResponse>> GetBusinessObjectivesAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Null when the id is not a functional/non-functional requirement.
    Task<IReadOnlyList<TraceRef>?> SetBusinessObjectivesAsync(Guid requirementId, IReadOnlyList<Guid> objectiveIds, CancellationToken cancellationToken = default);
}
