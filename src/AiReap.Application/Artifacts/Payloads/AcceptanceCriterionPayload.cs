namespace AiReap.Application.Artifacts.Payloads;

// §13 — ArtifactType.AcceptanceCriterion.
public class AcceptanceCriterionPayload
{
    public string Given { get; set; } = string.Empty;
    public string When { get; set; } = string.Empty;
    public string Then { get; set; } = string.Empty;
    public string Kind { get; set; } = "positive"; // positive|negative|boundary
}
