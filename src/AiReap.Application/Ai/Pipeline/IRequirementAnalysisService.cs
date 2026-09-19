namespace AiReap.Application.Ai.Pipeline;

// §7/§8 — turns raw input into actors/capabilities/data/notes plus persisted
// ClarificationQuestion artifacts for anything the AI flags as missing.
public interface IRequirementAnalysisService
{
    Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
