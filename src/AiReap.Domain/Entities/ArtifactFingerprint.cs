using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// The normalized-content fingerprint of an artifact as AI requirement analysis/generation created
// it (ArtifactFingerprints.For*), kept in its own table so the database itself rejects a second
// identical artifact for the same requirement source and type - two concurrent runs, or a retried
// commit - while the Artifacts table stays unchanged. Written once, when the artifact is created,
// and never updated: editing an artifact creates a new version of the same artifact, which the
// fingerprint does not constrain. Artifacts created before this table existed have no row.
//
// RequirementSourceId is copied, not a foreign key: deleting a source sets Artifact.RequirementSourceId
// to null, and a second cascade path from RequirementSources is not allowed by SQL Server.
public class ArtifactFingerprint
{
    public Guid ArtifactId { get; set; }
    public Guid RequirementSourceId { get; set; }
    public ArtifactType ArtifactType { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
