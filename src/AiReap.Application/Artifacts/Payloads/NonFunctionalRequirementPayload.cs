namespace AiReap.Application.Artifacts.Payloads;

// §10 — ArtifactType.NonFunctionalRequirement.
// AssumptionStatus is force-set server-side (never trusted from the AI response) —
// see RequirementGenerationService and REAP-011/REAP-045.
public class NonFunctionalRequirementPayload
{
    public string Category { get; set; } = string.Empty; // Performance|Security|Availability|Scalability|Usability|Maintainability|Compliance|Observability
    public string? Description { get; set; }
    public string? TargetValue { get; set; }
    public string? AssumptionStatus { get; set; } // "confirmed" | "proposed_assumption" — null when there's no TargetValue to qualify
}
