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

    // §26 — project document excerpts this requirement is grounded in, e.g. "notes.pdf#3".
    public List<string> SourceReferences { get; set; } = new();

    // Requirement IDs written in the requirement source that this requirement comes from
    // (SrsIdExtractor; validated against the text the model was given), the IDs it depends on
    // (anywhere in the source; turned into DependsOn links), and the source section it sits under.
    public List<string> SourceIds { get; set; } = new();
    public List<string> DependsOnSourceIds { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSection { get; set; }

    // P0-2 — the model's classification, stored as given and never changed by the server; a human
    // may reclassify it by an edit (a versioned decision). "explicit" | "derived" | "assumed" | null.
    public string? RequirementOrigin { get; set; }
    public string? DerivationNote { get; set; }
    public List<string> AssumedClarifications { get; set; } = new();

    // The model's verbatim quotes with the server's result for each (RequirementEvidence). Read-only
    // for humans, like the fields below (RequirementEvidenceFields.SanitizeEdit).
    public List<EvidenceQuotePayload> Evidence { get; set; } = new();

    // Server-owned. EvidenceStatus: "verified" | "unverified" | "none". UnsupportedNumbers: numbers in
    // the claim (preconditions, inputs, processing, expected result) not in any verified quote - a
    // warning. EvidenceCheck: the checker version; null on records made before evidence checks.
    public string? EvidenceStatus { get; set; }
    public List<string> UnsupportedNumbers { get; set; } = new();
    public string? EvidenceCheck { get; set; }
}
