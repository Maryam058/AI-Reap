namespace AiReap.Application.Artifacts.Payloads;

// §17 — ArtifactType.DataEntity.
public class DataEntityPayload
{
    public List<DataEntityField> Fields { get; set; } = new();
    public List<string> Keys { get; set; } = new();
    public string? Relationships { get; set; }
    public List<string> Indexes { get; set; } = new();
}

public class DataEntityField
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool Required { get; set; }
}
