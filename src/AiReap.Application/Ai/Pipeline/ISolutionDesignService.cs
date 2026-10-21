using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §16 — architecture-level design recommendation for a requirement source's approved
// functional requirements. Always editable/reviewable, never auto-applied.
public interface ISolutionDesignService
{
    Task<ArtifactResponse> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);

    // G2 — GenerateAsync, saying whether the design was created or the same design already existed (and
    // was returned unchanged). The default, for implementations that do not deduplicate, counts it created.
    async Task<(ArtifactResponse Design, bool Created)> GenerateWithOutcomeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default) =>
        (await GenerateAsync(requirementSourceId, cancellationToken), true);
}
