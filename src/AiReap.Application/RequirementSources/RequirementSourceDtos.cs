using AiReap.Domain.Enums;

namespace AiReap.Application.RequirementSources;

public record CreateRequirementSourceRequest(RequirementSourceType SourceType, string RawText, string? OriginalFileName);

public record RequirementSourceResponse(
    Guid Id,
    Guid ProjectId,
    RequirementSourceType SourceType,
    string RawText,
    string? OriginalFileName,
    DateTime CreatedAt,
    OriginalDocumentInfo? OriginalDocument = null);

// That the source's original PDF/DOCX is kept, and what the viewer needs to show it. Only on a single
// source read by a caller who may read raw sources; never the storage key or location.
// PageLineStarts: PDF only, the RawText line each page starts on.
public record OriginalDocumentInfo(string Format, long Length, IReadOnlyList<int>? PageLineStarts, DateTime StoredAt);

public enum OriginalDocumentStatus
{
    Found,

    // No such source, or one in a project the caller is not a member of: the two are not told apart.
    SourceNotFound,

    // The source has no kept original (pasted text, a TXT upload, or uploaded before originals were kept).
    NotRetained,

    // An original was kept but its file is gone or fails its integrity check.
    Unavailable,
}

// AttachOriginalAsync: what became of a file offered as an existing source's original.
public enum AttachOriginalStatus
{
    Attached,

    // As OriginalDocumentStatus.SourceNotFound.
    SourceNotFound,

    // The source already has an original, or was not uploaded as a PDF/DOCX of this format.
    NotApplicable,

    // The file's text is not the source's text: it is a different document, or a different version.
    TextMismatch,
}

public record AttachOriginalResult(AttachOriginalStatus Status, RequirementSourceResponse? Source = null);

public record OriginalDocumentResult(OriginalDocumentStatus Status, byte[]? Content = null, string? ContentType = null, string? FileName = null);
