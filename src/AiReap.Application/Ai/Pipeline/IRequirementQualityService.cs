namespace AiReap.Application.Ai.Pipeline;

// §14 — flags ambiguity, incompleteness, inconsistency, non-testability, duplication,
// contradiction, missing dependency, undefined terminology, excessive complexity, unclear
// actor/outcome across a requirement source's FR/NFR/BusinessRule artifacts.
public interface IRequirementQualityService
{
    Task<IReadOnlyList<QualityFinding>> AnalyzeAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
