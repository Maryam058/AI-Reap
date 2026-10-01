namespace AiReap.Application.Lifecycle;

public interface ILifecycleEvidenceService
{
    // Exit checks for all eight lifecycle stages of one project, in lifecycle order.
    Task<IReadOnlyList<StageEvidence>> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    // The same checks with their stable keys and what blocks each failing one (every blocking
    // artifact, before visibility filtering - ExitCheckGuidanceService applies ArtifactVisibility).
    Task<IReadOnlyList<StageCheckEvidence>> GetDetailedAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Batched for list views. Callers pass only project ids the current user may already see;
    // no membership check happens here.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<StageEvidence>>> GetManyAsync(
        IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default);
}
