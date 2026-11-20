using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

public record AnalyzeRequirementResult(
    IReadOnlyList<string> Actors,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> DataElements,
    IReadOnlyList<string> Notes,
    // The questions this run created (empty when the stored analysis was reused).
    IReadOnlyList<ArtifactResponse> ClarificationQuestions,
    // Questions not saved because the same question already exists for this source.
    int SkippedDuplicates = 0,
    // P0-7 — whether the source was sent whole or in parts, and which models served it. Response-only.
    RequirementCompleteness? Completeness = null,
    // True when no AI call was made: the stored analysis for this source was returned (RequirementAnalysisRecord).
    bool Reused = false,
    // Why the stored analysis may no longer reflect the source or the current rules; regenerate to refresh it.
    IReadOnlyList<string>? StaleReasons = null,
    string? AnalysisVersion = null,
    DateTime? AnalyzedAt = null,
    // Whether every line and section of the source was sent to the AI (AnalysisCoverage).
    AnalysisCoverageReport? Coverage = null,
    // Candidate questions the deterministic validator turned down, with the reasons.
    IReadOnlyList<RejectedClarification>? RejectedCandidates = null,
    AnalysisRetrievalReport? Retrieval = null,
    // Regeneration only: how the new run relates to the questions already saved.
    AnalysisReconciliation? Reconciliation = null,
    // Rejected candidates whose possible gap no question covers, kept for a reviewer (never saved as questions).
    IReadOnlyList<HeldClarification>? HeldForReview = null)
{
    public bool Stale => StaleReasons is { Count: > 0 };
}
