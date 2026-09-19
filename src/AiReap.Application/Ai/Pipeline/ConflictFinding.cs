using AiReap.Domain.Enums;

namespace AiReap.Application.Ai.Pipeline;

// §15 — a potential duplicate/conflict pair. Persisted as an ArtifactRelationship
// (RelationshipType.DuplicateOf or ConflictsWith) but never auto-resolved — the pair is
// always surfaced as "Human Resolution Required".
public record ConflictFinding(
    Guid ArtifactAId, string ArtifactACode, string ArtifactATitle,
    Guid ArtifactBId, string ArtifactBCode, string ArtifactBTitle,
    RelationshipType RelationshipType, string Reason);
