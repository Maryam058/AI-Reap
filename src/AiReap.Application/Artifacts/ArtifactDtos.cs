using System.Text.Json;
using AiReap.Domain.Enums;

namespace AiReap.Application.Artifacts;

public record ArtifactResponse(
    Guid Id,
    Guid ProjectId,
    ArtifactType ArtifactType,
    string Code,
    string Title,
    ArtifactPriority? Priority,
    ArtifactStatus Status,
    ArtifactOrigin Origin,
    Guid? RequirementSourceId,
    JsonElement Data,
    int CurrentVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ArtifactVersionResponse(
    int VersionNumber,
    JsonElement Data,
    string ChangedByUserId,
    DateTime ChangedAt,
    string? Reason,
    ArtifactOrigin Origin);

public record ArtifactRelationshipResponse(
    Guid RelatedArtifactId,
    string RelatedArtifactCode,
    string RelatedArtifactTitle,
    ArtifactType RelatedArtifactType,
    RelationshipType RelationshipType,
    bool IsOutgoing);

// A human edit to an existing artifact. Title/Priority/Data are all optional — only the
// fields the caller sends are changed. Sending Data replaces it wholesale (the frontend
// always sends the full typed payload back, never a partial patch of it).
public record UpdateArtifactRequest(
    string? Title,
    ArtifactPriority? Priority,
    JsonElement? Data,
    string? Reason);

public record UpdateArtifactStatusRequest(ArtifactStatus Status, string? Comment);

public record ClarificationAnswerRequest(string Answer, bool NotApplicable);
