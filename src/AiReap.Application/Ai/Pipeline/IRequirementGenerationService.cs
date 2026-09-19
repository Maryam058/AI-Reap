namespace AiReap.Application.Ai.Pipeline;

// §9/§10 — generates FunctionalRequirement and NonFunctionalRequirement artifacts from a
// requirement source plus whatever clarification questions have been answered so far.
public interface IRequirementGenerationService
{
    Task<GenerateRequirementsResult> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
