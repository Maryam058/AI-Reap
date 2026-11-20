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
    DateTime UpdatedAt,
    int? ApprovedVersion = null,
    // §22 — upstream changes this artifact has not been reviewed against yet.
    int OpenImpactNoticeCount = 0,
    // The artifact's author (who created or generated it); e.g. may not confirm their own NFR target.
    string? CreatedByUserId = null);

// Title/Priority/Status are null for versions recorded before full snapshots existed.
public record ArtifactVersionResponse(
    int VersionNumber,
    JsonElement Data,
    string ChangedByUserId,
    DateTime ChangedAt,
    string? Reason,
    ArtifactOrigin Origin,
    string? Title = null,
    ArtifactPriority? Priority = null,
    ArtifactStatus? Status = null);

// §24 — one human review decision, tied to the version it was made on.
public record ArtifactReviewResponse(
    Guid Id,
    string ReviewerUserId,
    ReviewDecision Decision,
    string? Comment,
    DateTime ReviewedAt,
    int? VersionNumber,
    // The role the reviewer acted under (e.g. Developer for a §17 design review); null on older rows.
    string? ReviewerRole = null);

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

// Version (S1): the version the reviewer decided on. When given, an approval or rejection of any other
// version is refused (409); omitted, the decision applies to the current version, as before.
public record UpdateArtifactStatusRequest(ArtifactStatus Status, string? Comment, int? Version = null);

public record ClarificationAnswerRequest(string Answer, bool NotApplicable);

// The version the confirmer reviewed; confirming anything but the current version is refused.
// Who confirms, when, and the resulting status are decided by the server.
public record ConfirmNfrTargetRequest(int? Version);

public record CommentReviewRequest(string? Comment);

// REAP-046 — the User Story that the story in the route depends on.
public record AddDependencyRequest(Guid DependsOnArtifactId);
