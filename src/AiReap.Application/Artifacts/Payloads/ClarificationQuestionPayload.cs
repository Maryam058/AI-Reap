namespace AiReap.Application.Artifacts.Payloads;

// Deserialized/serialized to Artifact.DataJson for ArtifactType.ClarificationQuestion.
// See docs/architecture/ADR-002-data-model.md.
public class ClarificationQuestionPayload
{
    public string Question { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string ClarificationStatus { get; set; } = "Open"; // Open|Answered|Resolved|NotApplicable
    public string? Answer { get; set; }

    // Server-owned: the places in Reason that resolve to the requirement source (SourceReferences).
    // Null on a question made before references were resolved - those are resolved when read
    // (ClarificationService), never invented; an empty list means none of Reason resolved.
    public List<SourceReferencePayload>? SourceReferences { get; set; }

    // Set by ClarificationCandidateValidator on questions created from RequirementAnalysis-v3 onwards;
    // null on older questions. Kind: missing | ambiguous | contradiction | undefined-rule.
    public string? Kind { get; set; }

    // Why the answer matters to requirements, design, implementation or acceptance (the model's words).
    public string? Impact { get; set; }

    // Server-owned: the model's evidence quotes that were found verbatim in the source, located there
    // (a contradiction carries both conflicting statements). Never the model's unverified claim.
    public List<SourceReferencePayload>? SourceEvidence { get; set; }

    // Server-owned: what the question concerns - the requirement IDs or sections its evidence sits in.
    public List<string>? RequirementRefs { get; set; }

    // Server-owned, set when the question is read (ClarificationSourceReferences.WithSourceLinks): how
    // far it is linked to its source - "passage" (located evidence to highlight), "section" (only its
    // reason's section links), or "unresolved" (no place in the source could be verified).
    public string? SourceLinkStatus { get; set; }

    // Server-owned: project document passages retrieved for the question (KnowledgeRetriever). Shown to
    // reviewers as material that may bear on it; never used to accept or reject the question.
    public List<RelatedDocumentPassagePayload>? RelatedDocuments { get; set; }
}

public class RelatedDocumentPassagePayload
{
    public string Reference { get; set; } = string.Empty; // "notes.pdf#3"
    public float Score { get; set; }
    public string Excerpt { get; set; } = string.Empty;
}
