namespace AiReap.Application.Artifacts;

// Implementation and verification evidence (see ArtifactWorkRecord). Throws KeyNotFoundException,
// ProjectAccessDeniedException, UnauthorizedAccessException (wrong role for the artifact type),
// RequestValidationException (outcome doesn't fit the artifact type) and ConflictException
// (the task/test case isn't approved).
public interface IArtifactWorkService
{
    Task<ArtifactWorkRecordResponse> RecordAsync(Guid artifactId, RecordWorkRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactWorkRecordResponse>> GetAsync(Guid artifactId, CancellationToken cancellationToken = default);
}
