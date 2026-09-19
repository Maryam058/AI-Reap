using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §17 — proposes data entities from a requirement source's approved functional
// requirements. Requires developer review before use, per the spec.
public interface IDatabaseDesignService
{
    Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
