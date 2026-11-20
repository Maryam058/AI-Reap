namespace AiReap.Application.Sdlc;

public interface ISdlcProgressService
{
    Task<IReadOnlyList<SdlcPhaseProgress>> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Batched for list views (projects page, cross-project dashboard). Callers pass only project
    // ids the current user may already see; no membership check happens here.
    Task<IReadOnlyList<ProjectSdlcProgress>> GetManyAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default);
}
