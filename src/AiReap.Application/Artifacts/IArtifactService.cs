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

    // Null if the artifact doesn't exist or isn't visible; UnauthorizedAccessException (not a BA or
    // Reviewer, or the NFR's author), RequestValidationException (not an NFR, no target, no
    // version), ConflictException (stale version, rejected, already confirmed).
    Task<ArtifactResponse?> ConfirmNfrTargetAsync(Guid id, ConfirmNfrTargetRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactVersionResponse>> GetVersionsAsync(Guid id, CancellationToken cancellationToken = default);

    // Returns null if the artifact doesn't exist; RequestValidationException if the comment is empty.
    Task<ArtifactReviewResponse?> AddCommentReviewAsync(Guid id, string? comment, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactReviewResponse>> GetReviewsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactRelationshipResponse>> GetRelationshipsAsync(Guid id, CancellationToken cancellationToken = default);

    // REAP-046 — story `id` -DependsOn-> story `request.DependsOnArtifactId`. Null if the dependent
    // story doesn't exist or isn't visible; RequestValidationException (not user stories, other
    // project, self), ConflictException (already linked, would form a cycle).
    Task<ArtifactRelationshipResponse?> AddDependencyAsync(Guid id, AddDependencyRequest request, CancellationToken cancellationToken = default);

    // False if the story or that DependsOn link doesn't exist.
    Task<bool> RemoveDependencyAsync(Guid id, Guid targetId, CancellationToken cancellationToken = default);
}
