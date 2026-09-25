namespace AiReap.Application.Lifecycle;

public interface ILifecycleEvidenceService
{
    // Exit checks for all eight lifecycle stages of one project, in lifecycle order.
    Task<IReadOnlyList<StageEvidence>> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Batched for list views. Callers pass only project ids the current user may already see;
    // no membership check happens here.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<StageEvidence>>> GetManyAsync(
        IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default);
}
