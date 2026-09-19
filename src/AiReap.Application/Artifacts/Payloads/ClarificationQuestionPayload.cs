namespace AiReap.Application.Artifacts.Payloads;

// Deserialized/serialized to Artifact.DataJson for ArtifactType.ClarificationQuestion.
// See docs/architecture/ADR-002-data-model.md.
public class ClarificationQuestionPayload
{
    public string Question { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string ClarificationStatus { get; set; } = "Open"; // Open|Answered|Resolved|NotApplicable
    public string? Answer { get; set; }
}
