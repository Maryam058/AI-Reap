using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// The generalized artifact spine — see docs/architecture/ADR-002-data-model.md.
// One table for every generated SDLC concept (FR, NFR, business rule, user story,
// acceptance criterion, clarification question, design artifact, API spec, data entity,
// implementation task, test case). Type-specific fields live in DataJson and are
// deserialized into a typed payload (AiReap.Application.Artifacts.Payloads) by the
// Application layer — the DB column stays generic so new artifact types never need a migration.
public class Artifact
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public ArtifactType ArtifactType { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ArtifactPriority? Priority { get; set; }
    public ArtifactStatus Status { get; set; } = ArtifactStatus.AiGenerated;
    public ArtifactOrigin Origin { get; set; } = ArtifactOrigin.Ai;

    public Guid? RequirementSourceId { get; set; }
    public RequirementSource? RequirementSource { get; set; }

    // Type-specific structured data (see ADR-002 for the field list per ArtifactType).
    public string DataJson { get; set; } = "{}";

    public int CurrentVersion { get; set; } = 1;

    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ArtifactVersion> Versions { get; set; } = new List<ArtifactVersion>();
    public ICollection<ArtifactReview> Reviews { get; set; } = new List<ArtifactReview>();
}
