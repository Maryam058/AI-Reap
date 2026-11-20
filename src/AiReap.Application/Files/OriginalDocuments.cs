using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiReap.Application.Files;

// Private storage for the original PDF/DOCX of an uploaded requirement source
// (docs/SOURCE_DOCUMENT_RETENTION.md). Keys are made by the server (NewKey) and are never a file
// name or anything a user sent; an implementation must refuse any other key. Nothing here is served
// directly: the bytes reach a user only through RequirementSourceService.GetOriginalAsync, which
// checks project membership and the raw-source rule (S2) first.
public interface IOriginalDocumentStore
{
    // Stores the bytes under a new key; the key is not visible until the bytes are complete.
    Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    // The stored bytes, or null when nothing is stored under the key.
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);

    // Removes the key's bytes; nothing happens when there are none.
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    // Every stored key last written before the cutoff (OrphanSweep), and any temporary leftovers of
    // interrupted writes older than it, which an implementation removes itself.
    Task<IReadOnlyList<string>> ListKeysWrittenBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    // Whether this store belongs to the given owner (a database): claims it when no one has yet. A
    // store pointed at by two databases must never be swept by either, or each would take the other's
    // files for orphans.
    Task<bool> IsOwnedByAsync(string owner, CancellationToken cancellationToken = default);
}

public static class OriginalDocumentKeys
{
    private static readonly Regex Shape = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static string NewKey() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? key) => key is not null && Shape.IsMatch(key);
}

// Configuration section "OriginalDocuments".
public class OriginalDocumentOptions
{
    public const string SectionName = "OriginalDocuments";

    // When false, new uploads keep only their extracted text (the behaviour before originals were
    // kept); originals already stored are still served and swept.
    public bool Enabled { get; set; } = true;

    // Where the files are kept. Empty: %LOCALAPPDATA%/AiReap/source-documents/{database name} (outside
    // the repository and outside anything the API or the web app serves). Set it explicitly on a
    // server; one directory per database.
    public string? RootPath { get; set; }

    // The largest upload accepted, in bytes (also the request body limit of the upload endpoint).
    public long MaxUploadBytes { get; set; } = 5 * 1024 * 1024;

    // Null: an original is kept as long as its source (the same as the source's text, §6). A number:
    // originals older than that many days are deleted by OriginalDocumentJanitor; the source, its text,
    // its questions and their references stay, and the viewer falls back to the text.
    public int? RetentionDays { get; set; }

    // How often OriginalDocumentJanitor runs; 0 turns it off.
    public int CleanupIntervalMinutes { get; set; } = 360;

    // A stored file is an orphan only when no source references it and it is older than this, so an
    // upload whose database save is still in progress is never mistaken for one.
    public int OrphanGraceMinutes { get; set; } = 60;
}

public static class OriginalDocumentFormats
{
    public const string Pdf = "pdf";
    public const string Docx = "docx";

    public static string ContentType(string format) => format switch
    {
        Pdf => "application/pdf",
        Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a stored document format."),
    };

    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

// What an uploaded file's first bytes say it is, whatever its name or MIME header claims.
public static class DocumentSignature
{
    public enum Kind { Unknown, Pdf, Zip, Ole, Executable }

    public static Kind Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("PK\u0003\u0004"u8)) return Kind.Zip;
        if (content.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return Kind.Ole; // .doc, encrypted .docx
        if (content.StartsWith("MZ"u8) || content.StartsWith("\u007FELF"u8)) return Kind.Executable;
        // PDF readers accept a header after a little leading junk; 1024 bytes is the conventional window.
        return content[..Math.Min(content.Length, 1024)].IndexOf("%PDF-"u8) >= 0 ? Kind.Pdf : Kind.Unknown;
    }
}

// The upload failed validation: the message says why, and is safe to show the user (it never quotes
// the file's content).
public class InvalidDocumentException(string message) : Exception(message);

// The text extracted from an upload, and what is needed to keep and show the original.
// Format is null for TXT (its text is the file). PageLineStarts: PDF only, see RequirementSourceFile.
// LegacyText: DOCX only, the text as uploads were extracted before field instructions were dropped
// ("Actors and RolesPAGEREF _Toc123 \h 9"), so an original can be matched to a source stored then.
// ParagraphText: DOCX only, the text as uploads were extracted after that and before table rows,
// numbering, notes, headers and footers were kept (one line per paragraph), for the same purpose.
public record ExtractedDocument(
    string Text, string? Format, IReadOnlyList<int>? PageLineStarts, string? LegacyText = null, string? ParagraphText = null);

// An original to keep with a new source.
public record OriginalDocumentUpload(ReadOnlyMemory<byte> Content, string Format, IReadOnlyList<int>? PageLineStarts);

public static class OriginalDocumentIntake
{
    // Stores the bytes, then runs the database commit. A commit that fails or is cancelled removes the
    // bytes again, so a failed upload leaves neither a source without its file nor a file without its
    // source. If that removal fails too, the file is an orphan that OrphanSweep removes later; the
    // commit's own failure is what is reported either way.
    public static async Task StoreThenCommitAsync(
        IOriginalDocumentStore store, string key, ReadOnlyMemory<byte> content, Func<Task> commit, ILogger logger,
        CancellationToken cancellationToken)
    {
        await store.SaveAsync(key, content, cancellationToken);
        try
        {
            await commit();
        }
        catch
        {
            try
            {
                await store.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                logger.LogWarning(cleanup, "Could not remove the stored original {StorageKey} after a failed upload; the orphan sweep will remove it.", key);
            }

            throw;
        }
    }
}

public static class OrphanSweep
{
    // Removes stored files that no source references. Only a store that belongs to this database
    // (IsOwnedByAsync) is swept - null when it belongs to another, and nothing is removed. Only keys
    // older than the cutoff are considered, and only after the referenced keys were read successfully:
    // a failing lookup (for example a database the migration has not reached yet) removes nothing.
    public static async Task<int?> RunAsync(
        IOriginalDocumentStore store,
        string owner,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<ISet<string>>> referencedKeys,
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        if (!await store.IsOwnedByAsync(owner, cancellationToken))
        {
            return null;
        }

        var candidates = (await store.ListKeysWrittenBeforeAsync(cutoffUtc, cancellationToken)).Where(OriginalDocumentKeys.IsValid).ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var referenced = await referencedKeys(candidates, cancellationToken);
        var removed = 0;
        foreach (var key in candidates.Where(k => !referenced.Contains(k)))
        {
            await store.DeleteAsync(key, cancellationToken);
            removed++;
        }

        return removed;
    }
}
