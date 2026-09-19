namespace AiReap.Application.Artifacts.Payloads;

// §9 — ArtifactType.FunctionalRequirement.
public class FunctionalRequirementPayload
{
    public string Actor { get; set; } = string.Empty;
    public string? Preconditions { get; set; }
    public string? Inputs { get; set; }
    public string? Processing { get; set; }
    public string? ExpectedResult { get; set; }
    public List<string> Dependencies { get; set; } = new();
}
