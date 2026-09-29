using System.Text.Json.Serialization;

namespace AiReap.Application.Artifacts.Payloads;

// §10 — ArtifactType.NonFunctionalRequirement.
// AssumptionStatus is force-set server-side (never trusted from the AI response) —
// see RequirementGenerationService and REAP-011/REAP-045. Nor is it trusted from a human edit:
// only the NFR confirmation action (ArtifactService.ConfirmNfrTargetAsync) sets "confirmed", and
// an edit that changes the target drops it back to "proposed_assumption" (NfrAssumption).
public class NonFunctionalRequirementPayload
{
    public string Category { get; set; } = string.Empty; // Performance|Security|Availability|Scalability|Usability|Maintainability|Compliance|Observability
    public string? Description { get; set; }
    public string? TargetValue { get; set; }
    public string? AssumptionStatus { get; set; } // "confirmed" | "proposed_assumption" — null when there's no TargetValue to qualify

    // Who confirmed the target and when; set only alongside AssumptionStatus = "confirmed".
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmedByUserId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ConfirmedAt { get; set; }
}
