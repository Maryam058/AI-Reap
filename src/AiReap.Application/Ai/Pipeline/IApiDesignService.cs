using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §18 — proposes API endpoint contracts from a requirement source's approved functional
// requirements.
public interface IApiDesignService
{
    Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
