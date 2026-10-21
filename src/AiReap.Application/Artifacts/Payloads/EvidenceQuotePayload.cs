namespace AiReap.Application.Artifacts.Payloads;

// One verbatim quote a generated requirement cites as its evidence (RequirementEvidence). From, Quote,
// SourceId and Ref are the model's; Status, Reason, MatchMode and MatchCount are decided by the server
// when the requirement is generated and are never taken from a human edit (RequirementEvidenceFields).
// "verified" means only that the quoted words are written in the cited text - not that the
// requirement follows from them or is correct.
public class EvidenceQuotePayload
{
    public string? From { get; set; }      // "srs" | "clarification" | "excerpt"
    public string Quote { get; set; } = string.Empty;
    public string? SourceId { get; set; }  // the requirement ID the quote sits under (srs only), e.g. "FR-012"
    public string? Ref { get; set; }       // the clarification code ("CQ-003") or excerpt label ("notes.pdf#3")

    // Server-owned.
    public string? Status { get; set; }    // "verified" | "unverified" | "none"
    public string? Reason { get; set; }    // not_found | altered | too_short | too_long | unknown_source_id | not_at_cited_id | source_unavailable | unknown_reference | clarification_unanswered | invalid_source_type
    public string? MatchMode { get; set; } // "strict" | "spacing_tolerant"
    public int MatchCount { get; set; }
}
