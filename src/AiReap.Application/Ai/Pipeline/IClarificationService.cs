using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §8 — question lifecycle (Open/Answered/Resolved/NotApplicable) lives in each
// ClarificationQuestion artifact's DataJson; answering goes through
// IArtifactService.AnswerClarificationAsync. This service is the read side.
public interface IClarificationService
{
    Task<IReadOnlyList<ArtifactResponse>> GetForRequirementSourceAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
