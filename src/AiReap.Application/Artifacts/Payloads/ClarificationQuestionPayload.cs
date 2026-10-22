namespace AiReap.Application.Artifacts.Payloads;

// Deserialized/serialized to Artifact.DataJson for ArtifactType.ClarificationQuestion.
// See docs/architecture/ADR-002-data-model.md.
public class ClarificationQuestionPayload
{
    public string Question { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string ClarificationStatus { get; set; } = "Open"; // Open|Answered|Resolved|NotApplicable
    public string? Answer { get; set; }

    // Server-owned: the places in Reason that resolve to the requirement source (SourceReferences).
    // Null on a question made before references were resolved - those are resolved when read
    // (ClarificationService), never invented; an empty list means none of Reason resolved.
    public List<SourceReferencePayload>? SourceReferences { get; set; }
}
