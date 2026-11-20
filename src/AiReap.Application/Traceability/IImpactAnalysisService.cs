using AiReap.Domain.Entities;

namespace AiReap.Application.Traceability;

// §22 — impact analysis never modifies the affected artifacts. AnalyzeAsync is the on-demand view;
// StageNoticesForChangeAsync runs automatically when approved content changes and records a
// persistent notice on each downstream artifact until a person acknowledges it. It stages the
// notices without saving; the caller saves them together with the change.
public interface IImpactAnalysisService
{
    Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default);

    Task<int> StageNoticesForChangeAsync(Artifact changed, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForProjectAsync(Guid projectId, bool includeAcknowledged, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    Task<ImpactNoticeResponse?> AcknowledgeAsync(Guid noticeId, string? note, CancellationToken cancellationToken = default);
}
