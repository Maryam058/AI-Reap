namespace AiReap.Domain.Entities;

// §22 — "the system must not silently modify approved downstream artifacts". When approved content
// changes, every downstream artifact gets one of these instead of being touched: a persistent,
// visible "an upstream artifact you depend on changed - review me" flag, open until a person
// acknowledges it. The affected artifact itself (content, status, versions) is never modified.
public class ArtifactImpactNotice
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    // The artifact that changed, and the new version that triggered this notice.
    public Guid SourceArtifactId { get; set; }
    public int SourceVersion { get; set; }

    public Guid AffectedArtifactId { get; set; }

    // How the affected artifact is reached from the source, e.g. "US-001 derived from FR-001".
    public string Path { get; set; } = string.Empty;

    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public string? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgementNote { get; set; }
}
