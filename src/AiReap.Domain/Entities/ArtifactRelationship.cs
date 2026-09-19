using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §21 traceability graph, §11 rule-to-requirement links, §15 duplicate/conflict records.
// This single table is queried both as the traceability matrix and as the conflict list —
// no separate TraceabilityMatrix or ConflictReport table.
public class ArtifactRelationship
{
    public Guid Id { get; set; }

    public Guid SourceArtifactId { get; set; }
    public Artifact? SourceArtifact { get; set; }

    public Guid TargetArtifactId { get; set; }
    public Artifact? TargetArtifact { get; set; }

    public RelationshipType RelationshipType { get; set; }
    public DateTime CreatedAt { get; set; }
}
