namespace AiReap.Domain.Entities;

// The original PDF/DOCX a requirement source was extracted from, kept so the source viewer can show
// the document itself (docs/SOURCE_DOCUMENT_RETENTION.md). One per source at most: a source pasted as
// text, a TXT upload (its text is the file) and a source uploaded before originals were kept have none.
// The bytes live in private storage (IOriginalDocumentStore) under StorageKey, which the server makes
// and never shows to a user; the uploaded file name stays on the source, as display text only.
public class RequirementSourceFile
{
    // Also the key: the source this file belongs to (and through it, the project).
    public Guid RequirementSourceId { get; set; }

    public string StorageKey { get; set; } = string.Empty;

    // "pdf" or "docx": detected from the content, not taken from the file name or MIME header.
    public string Format { get; set; } = string.Empty;

    public long Length { get; set; }

    // SHA-256 of the stored bytes, lower-case hex; checked on every read.
    public string Sha256 { get; set; } = string.Empty;

    // PDF only: the 0-based line of RequirementSource.RawText each page starts on, as a JSON array,
    // so a reference into the text can be turned into a page. Null for DOCX.
    public string? PageLineStartsJson { get; set; }

    public DateTime CreatedAt { get; set; }
}
