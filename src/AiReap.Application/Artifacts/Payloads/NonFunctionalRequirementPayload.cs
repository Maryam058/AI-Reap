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

    // Requirement IDs written in the requirement source that this NFR comes from, and the source
    // section it sits under (see FunctionalRequirementPayload).
    public List<string> SourceIds { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSection { get; set; }

    // P0-2 — origin and evidence, as on FunctionalRequirementPayload (the claim checked for
    // UnsupportedNumbers is the description and target value).
    public string? RequirementOrigin { get; set; }
    public string? DerivationNote { get; set; }
    public List<string> AssumedClarifications { get; set; } = new();
    public List<EvidenceQuotePayload> Evidence { get; set; } = new();
    public string? EvidenceStatus { get; set; }
    public List<string> UnsupportedNumbers { get; set; } = new();
    public string? EvidenceCheck { get; set; }

    // TargetOrigin is the model's claim about the target, kept as given: "stated" | "proposed" | null.
    // TargetSupport is the server's check of a "stated" claim: "quote_found" only when a verified SRS
    // quote contains the target's numbers and units, else "not_supported"; null when not claimed
    // stated. Neither changes AssumptionStatus: every target still needs a human confirmation.
    public string? TargetOrigin { get; set; }
    public string? TargetSupport { get; set; }
}
