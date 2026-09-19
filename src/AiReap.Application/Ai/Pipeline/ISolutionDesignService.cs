using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §16 — architecture-level design recommendation for a requirement source's approved
// functional requirements. Always editable/reviewable, never auto-applied.
public interface ISolutionDesignService
{
    Task<ArtifactResponse> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
