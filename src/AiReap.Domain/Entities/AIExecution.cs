namespace AiReap.Domain.Entities;

// §27 — AI audit trail. Deliberately has no chain-of-thought field: only structured
// application inputs/outputs and the human decision are ever stored, per the spec's
// explicit prohibition on persisting hidden provider reasoning.
public class AIExecution
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    public string OperationType { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? PromptTemplateVersion { get; set; }
    public string? InputReference { get; set; }
    public string OutputJson { get; set; } = "{}";
    public bool? Accepted { get; set; }
    public Guid? ProducedArtifactId { get; set; }
}
