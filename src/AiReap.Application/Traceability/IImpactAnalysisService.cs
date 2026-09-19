namespace AiReap.Application.Traceability;

// §22 — advisory only. Never modifies anything; the caller (a human) decides what to do
// with the list.
public interface IImpactAnalysisService
{
    Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default);
}
