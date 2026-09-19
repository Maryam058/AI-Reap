using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §24 — generalized approval trail for any artifact type.
public class ArtifactReview
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }
    public Artifact? Artifact { get; set; }

    public string ReviewerUserId { get; set; } = string.Empty;
    public ReviewDecision Decision { get; set; }
    public string? Comment { get; set; }
    public DateTime ReviewedAt { get; set; }
}
