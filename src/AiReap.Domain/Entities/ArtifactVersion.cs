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

    // Full snapshot, not just the payload: a title- or priority-only edit must still be visible
    // in history, and the status shows which version was the approved one. Null on rows written
    // before these columns existed.
    public string? Title { get; set; }
    public ArtifactPriority? Priority { get; set; }
    public ArtifactStatus? Status { get; set; }
    public string ChangedByUserId { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
    public string? Reason { get; set; }
    public ArtifactOrigin Origin { get; set; }
}
