using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §6 — raw input, retained verbatim forever. Never overwritten by AI-generated content,
// and deliberately not an Artifact: it doesn't go through the approval/versioning workflow.
public class RequirementSource
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public RequirementSourceType SourceType { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
