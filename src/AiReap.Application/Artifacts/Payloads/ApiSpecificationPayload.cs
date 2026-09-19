namespace AiReap.Application.Artifacts.Payloads;

// §18 — ArtifactType.ApiSpecification.
public class ApiSpecificationPayload
{
    public string Purpose { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string? Request { get; set; }
    public string? Response { get; set; }
    public string? Validation { get; set; }
    public string? Authorization { get; set; }
}
