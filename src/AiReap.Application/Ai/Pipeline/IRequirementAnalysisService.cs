namespace AiReap.Application.Ai.Pipeline;

// §7/§8 — turns raw input into actors/capabilities/data/notes plus persisted
// ClarificationQuestion artifacts for the material issues the AI raises and the validator accepts.
public interface IRequirementAnalysisService
{
    // Returns the stored analysis when there is one (no AI call, even when it is stale - the result says
    // why); otherwise analyses the source.
    Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);

    // regenerate: an explicit request to analyse again. New questions are reconciled with the saved ones:
    // duplicates are not added and nothing saved is changed or removed.
    Task<AnalyzeRequirementResult> AnalyzeAsync(Guid requirementSourceId, bool regenerate, CancellationToken cancellationToken = default);

    // The stored analysis, or null when the source has never been analysed. Never calls the AI.
    Task<AnalyzeRequirementResult?> GetLatestAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
