using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §12/§13 — user stories derived from a requirement source's functional requirements,
// and acceptance criteria derived from a specific user story. Both link back through
// ArtifactRelationship(DerivedFrom) for traceability (§21).
public interface IUserStoryService
{
    Task<IReadOnlyList<ArtifactResponse>> GenerateStoriesAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactResponse>?> GenerateAcceptanceCriteriaAsync(Guid userStoryArtifactId, CancellationToken cancellationToken = default);
}
