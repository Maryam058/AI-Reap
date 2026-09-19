using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §23 — one version-history table for every artifact type.
public class ArtifactVersion
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public Artifact? Artifact { get; set; }

    public int VersionNumber { get; set; }
    public string DataSnapshotJson { get; set; } = "{}";
    public string ChangedByUserId { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
    public string? Reason { get; set; }
    public ArtifactOrigin Origin { get; set; }
}
