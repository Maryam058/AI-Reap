using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §8 — question lifecycle (Open/Answered/Resolved/NotApplicable) lives in each
// ClarificationQuestion artifact's DataJson; answering goes through
// IArtifactService.AnswerClarificationAsync, and Resolved is set by
// ArtifactService.ResolveClarificationIfAnsweredAsync when a reviewer approves an answered
// question. This service is the read side.
//
// Intentional deviation, not an oversight (decided 2026-09-22): there is no separate
// ambiguity/contradiction-detection pass here. Question *generation* is folded into
// RequirementAnalysisService (§7) — it already satisfies §8's actual intent (missing/ambiguous
// information surfaces as a question rather than a fabricated detail, per §32), and a standalone
// pass would either re-run the same analysis prompt for no new capability, or need a materially
// different prompt whose value is speculative.
public interface IClarificationService
{
    Task<IReadOnlyList<ArtifactResponse>> GetForRequirementSourceAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
