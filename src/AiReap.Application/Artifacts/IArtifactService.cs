using AiReap.Domain.Enums;

namespace AiReap.Application.Artifacts;

public interface IArtifactService
{
    Task<IReadOnlyList<ArtifactResponse>> GetForProjectAsync(
        Guid projectId, ArtifactType? type, Guid? requirementSourceId, CancellationToken cancellationToken = default);

    Task<ArtifactResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ArtifactResponse?> UpdateAsync(Guid id, UpdateArtifactRequest request, CancellationToken cancellationToken = default);

    Task<ArtifactResponse?> UpdateStatusAsync(Guid id, UpdateArtifactStatusRequest request, CancellationToken cancellationToken = default);

    Task<ArtifactResponse?> AnswerClarificationAsync(Guid id, ClarificationAnswerRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactVersionResponse>> GetVersionsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactRelationshipResponse>> GetRelationshipsAsync(Guid id, CancellationToken cancellationToken = default);
}
