using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// Implementation / verification evidence: a Developer marking a task done, or QA recording a
// test outcome. Append-only; the latest record per artifact is its current outcome. Kept out of
// the artifact's content and status, so recording work never creates a version or touches approval.
public class ArtifactWorkRecord
{
    public Guid Id { get; set; }
    public Guid ArtifactId { get; set; }

    // The artifact version the work was done against.
    public int VersionNumber { get; set; }
    public WorkOutcome Outcome { get; set; }
    public string? Note { get; set; }

    public string RecordedByUserId { get; set; } = string.Empty;
    public string? RecordedByRole { get; set; }
    public DateTime RecordedAt { get; set; }
}
