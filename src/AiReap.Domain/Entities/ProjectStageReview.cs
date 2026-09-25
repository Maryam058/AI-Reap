using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// Decisions and comment-only reviews on a stage transition, mirroring ArtifactReview for artifacts.
public class ProjectStageReview
{
    public Guid Id { get; set; }
    public Guid TransitionId { get; set; }
    public ProjectStageTransition? Transition { get; set; }

    public string ReviewerUserId { get; set; } = string.Empty;
    public string? ReviewerRole { get; set; }
    public ReviewDecision Decision { get; set; }
    public string? Comment { get; set; }
    public DateTime ReviewedAt { get; set; }
}
