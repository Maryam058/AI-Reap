using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §5 — each project is an independent AI context.
public class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? BusinessProblem { get; set; }
    public string? Objectives { get; set; }
    public string? Scope { get; set; }
    public string? Domain { get; set; }
    public string? TargetUsers { get; set; }
    public string? TechnologyPreferences { get; set; }
    public string? Constraints { get; set; }
    public string? ExpectedTimeline { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;

    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ProjectStakeholder> Stakeholders { get; set; } = new List<ProjectStakeholder>();
}
