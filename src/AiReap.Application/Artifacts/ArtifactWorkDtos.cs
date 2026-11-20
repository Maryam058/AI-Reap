using AiReap.Domain.Enums;

namespace AiReap.Application.Artifacts;

public record RecordWorkRequest(WorkOutcome Outcome, string? Note);

public record ArtifactWorkRecordResponse(
    Guid Id,
    Guid ArtifactId,
    int VersionNumber,
    WorkOutcome Outcome,
    string? Note,
    string RecordedByUserId,
    string? RecordedByRole,
    DateTime RecordedAt);
