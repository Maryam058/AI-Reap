using AiReap.Application.Ai;

namespace AiReap.Application.RequirementSources;

public interface IRequirementSourceService
{
    Task<RequirementSourceResponse> CreateAsync(Guid projectId, CreateRequirementSourceRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RequirementSourceResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<RequirementSourceResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RequirementSourceResponse> CreateFromUploadAsync(Guid projectId, string fileName, string text, AiReap.Application.Files.OriginalDocumentUpload? original, CancellationToken cancellationToken = default);

    // The kept original PDF/DOCX, after the project-membership and raw-source (S2) checks.
    Task<OriginalDocumentResult> GetOriginalAsync(Guid id, CancellationToken cancellationToken = default);

    // Keeps a PDF/DOCX as the original of a source uploaded before originals were kept - only when its
    // text is exactly the source's text, so every line reference into the source still holds.
    Task<AttachOriginalResult> AttachOriginalAsync(Guid id, ReadOnlyMemory<byte> content, AiReap.Application.Files.ExtractedDocument extracted, CancellationToken cancellationToken = default);

    // P0-4 — null when the source does not exist.
    Task<SourceIdCoverage?> GetSourceIdCoverageAsync(Guid id, CancellationToken cancellationToken = default);
}
