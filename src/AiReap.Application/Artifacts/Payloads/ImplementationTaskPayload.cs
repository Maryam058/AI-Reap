namespace AiReap.Application.Artifacts.Payloads;

// §19 — ArtifactType.ImplementationTask.
public class ImplementationTaskPayload
{
    public string Description { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty; // e.g. Backend|Frontend|Database|Infrastructure
    public List<string> Dependencies { get; set; } = new();
    public string? SuggestedRole { get; set; }
    public string? Estimate { get; set; }
}
