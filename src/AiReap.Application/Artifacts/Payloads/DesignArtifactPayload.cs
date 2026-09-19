namespace AiReap.Application.Artifacts.Payloads;

// §16 — ArtifactType.DesignArtifact. APIs and DB entities are modeled separately
// (ApiSpecificationPayload, DataEntityPayload) — this covers the architecture-level content.
public class DesignArtifactPayload
{
    public string ArchitectureOverview { get; set; } = string.Empty;
    public List<string> Modules { get; set; } = new();
    public string? IntegrationPoints { get; set; }
    public string? Authentication { get; set; }
    public string? BackgroundProcessing { get; set; }
    public string? Caching { get; set; }
    public string? Logging { get; set; }
    public string? DeploymentConsiderations { get; set; }
}
