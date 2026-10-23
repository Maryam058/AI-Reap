using System.Text.Json;
using AiReap.Application.Ai;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Application.Files;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiReap.Application.RequirementSources;

public class RequirementSourceService : IRequirementSourceService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IOriginalDocumentStore _originals;
    private readonly OriginalDocumentOptions _originalOptions;
    private readonly ILogger<RequirementSourceService> _logger;

    public RequirementSourceService(
        IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess, IOriginalDocumentStore originals,
        OriginalDocumentOptions originalOptions, ILogger<RequirementSourceService>? logger = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _originals = originals;
        _originalOptions = originalOptions;
        _logger = logger ?? NullLogger<RequirementSourceService>.Instance;
    }

    public async Task<RequirementSourceResponse> CreateAsync(Guid projectId, CreateRequirementSourceRequest request, CancellationToken cancellationToken = default)
    {
        // §6 — the raw source is retained verbatim and is never overwritten by AI output.
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceType = request.SourceType,
            RawText = request.RawText,
            OriginalFileName = request.OriginalFileName,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = DateTime.UtcNow
        };

        _db.RequirementSources.Add(source);
        await _db.SaveChangesAsync(cancellationToken);

        return ToResponse(source);
    }

    // An upload: the extracted text as a new source, as CreateAsync, and - for a PDF/DOCX while
    // originals are kept (OriginalDocuments:Enabled) - the file itself, in private storage under a new
    // server-made key. Reported as created only once the source and its file row are saved together; a
    // save that fails removes the stored file again (OriginalDocumentIntake).
    public async Task<RequirementSourceResponse> CreateFromUploadAsync(
        Guid projectId, string fileName, string text, OriginalDocumentUpload? original, CancellationToken cancellationToken = default)
    {
        var source = new RequirementSource
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceType = RequirementSourceType.FileUpload,
            RawText = text,
            OriginalFileName = fileName,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = DateTime.UtcNow
        };
        _db.RequirementSources.Add(source);

        if (original is null || !_originalOptions.Enabled)
        {
            await SaveOrUndoAsync(cancellationToken);
            return ToResponse(source);
        }

        var file = new RequirementSourceFile
        {
            RequirementSourceId = source.Id,
            StorageKey = OriginalDocumentKeys.NewKey(),
            Format = original.Format,
            Length = original.Content.Length,
            Sha256 = OriginalDocumentFormats.Sha256(original.Content.Span),
            PageLineStartsJson = original.PageLineStarts is null ? null : JsonSerializer.Serialize(original.PageLineStarts),
            CreatedAt = source.CreatedAt
        };
        _db.RequirementSourceFiles.Add(file);

        try
        {
            await OriginalDocumentIntake.StoreThenCommitAsync(
                _originals, file.StorageKey, original.Content, () => SaveOrUndoAsync(cancellationToken), _logger, cancellationToken);
        }
        catch
        {
            ArtifactPersistence.Undo(_db); // a failed store write never reached the save
            throw;
        }

        return ToResponse(source, file);
    }

    // A failed save leaves nothing behind in this context for a later save to pick up.
    private async Task SaveOrUndoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            ArtifactPersistence.Undo(_db);
            throw;
        }
    }

    public async Task<IReadOnlyList<RequirementSourceResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var sources = await _db.RequirementSources
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        // S2 - the workspace groups artifacts by source, so a caller who may not read raw sources
        // still gets each source's id, type and date, but never its text or file name.
        return _currentUser.CanReadRawSources()
            ? sources.Select(s => ToResponse(s)).ToList()
            : sources.Select(s => new RequirementSourceResponse(s.Id, s.ProjectId, s.SourceType, string.Empty, null, s.CreatedAt)).ToList();
    }

    public async Task<RequirementSourceResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        if (!_currentUser.CanReadRawSources())
        {
            throw new UnauthorizedAccessException("Raw requirement sources are not available to your role; use the approved requirements.");
        }

        var file = await _db.RequirementSourceFiles.AsNoTracking().FirstOrDefaultAsync(f => f.RequirementSourceId == id, cancellationToken);
        return ToResponse(source, file);
    }

    // The kept original PDF/DOCX of a source, for the source viewer. The same rules as the source's
    // text: the caller must be a member of its project (or an Administrator) and may read raw sources
    // (S2, else UnauthorizedAccessException -> 403). A source in another project is reported exactly
    // like one that does not exist, so changing the ID in a request tells the caller nothing. The bytes
    // are checked against the length and SHA-256 recorded at upload before they are returned.
    public async Task<OriginalDocumentResult> GetOriginalAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.ProjectId, s.OriginalFileName })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null || !await _projectAccess.IsMemberAsync(source.ProjectId, cancellationToken))
        {
            return new OriginalDocumentResult(OriginalDocumentStatus.SourceNotFound);
        }

        if (!_currentUser.CanReadRawSources())
        {
            throw new UnauthorizedAccessException("Raw requirement sources are not available to your role; use the approved requirements.");
        }

        var file = await _db.RequirementSourceFiles.AsNoTracking().FirstOrDefaultAsync(f => f.RequirementSourceId == id, cancellationToken);
        if (file is null)
        {
            return new OriginalDocumentResult(OriginalDocumentStatus.NotRetained);
        }

        var content = OriginalDocumentKeys.IsValid(file.StorageKey) ? await _originals.ReadAsync(file.StorageKey, cancellationToken) : null;
        if (content is null)
        {
            _logger.LogWarning("The original document of requirement source {SourceId} is recorded but missing from storage.", id);
            return new OriginalDocumentResult(OriginalDocumentStatus.Unavailable);
        }

        if (content.LongLength != file.Length || OriginalDocumentFormats.Sha256(content) != file.Sha256)
        {
            _logger.LogWarning("The original document of requirement source {SourceId} failed its integrity check.", id);
            return new OriginalDocumentResult(OriginalDocumentStatus.Unavailable);
        }

        return new OriginalDocumentResult(
            OriginalDocumentStatus.Found, content, OriginalDocumentFormats.ContentType(file.Format), source.OriginalFileName);
    }

    // An original for a source that has none: a PDF/DOCX upload made before originals were kept. The
    // source's text is never changed - the file is accepted only when extracting it gives that text
    // (for a DOCX, as it is extracted now or as it was by an earlier extractor), so the viewer's line references into the
    // text find the same places in the file. Membership, S2 and OriginalDocuments:Enabled apply as for
    // an upload; stored and committed together as CreateFromUploadAsync does.
    public async Task<AttachOriginalResult> AttachOriginalAsync(
        Guid id, ReadOnlyMemory<byte> content, ExtractedDocument extracted, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null || !await _projectAccess.IsMemberAsync(source.ProjectId, cancellationToken))
        {
            return new AttachOriginalResult(AttachOriginalStatus.SourceNotFound);
        }

        if (!_currentUser.CanReadRawSources())
        {
            throw new UnauthorizedAccessException("Raw requirement sources are not available to your role; use the approved requirements.");
        }

        var expectedFormat = Path.GetExtension(source.OriginalFileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (!_originalOptions.Enabled || source.SourceType != RequirementSourceType.FileUpload || extracted.Format is null
            || extracted.Format != expectedFormat
            || await _db.RequirementSourceFiles.AnyAsync(f => f.RequirementSourceId == id, cancellationToken))
        {
            return new AttachOriginalResult(AttachOriginalStatus.NotApplicable);
        }

        var lines = SourceReferenceIndex.Lines(source.RawText);
        bool Same(string? text) => text is not null && SourceReferenceIndex.Lines(text).SequenceEqual(lines);
        if (!Same(extracted.Text) && !Same(extracted.LegacyText) && !Same(extracted.ParagraphText))
        {
            return new AttachOriginalResult(AttachOriginalStatus.TextMismatch);
        }

        var file = new RequirementSourceFile
        {
            RequirementSourceId = source.Id,
            StorageKey = OriginalDocumentKeys.NewKey(),
            Format = extracted.Format,
            Length = content.Length,
            Sha256 = OriginalDocumentFormats.Sha256(content.Span),
            PageLineStartsJson = extracted.PageLineStarts is null ? null : JsonSerializer.Serialize(extracted.PageLineStarts),
            CreatedAt = DateTime.UtcNow
        };
        _db.RequirementSourceFiles.Add(file);

        try
        {
            await OriginalDocumentIntake.StoreThenCommitAsync(
                _originals, file.StorageKey, content, () => SaveOrUndoAsync(cancellationToken), _logger, cancellationToken);
        }
        catch
        {
            ArtifactPersistence.Undo(_db);
            throw;
        }

        return new AttachOriginalResult(AttachOriginalStatus.Attached, ToResponse(source, file));
    }

    // P0-4 — read-only, computed now from the source text and the FRs/NFRs currently saved for it, so
    // it is right after a reload, a re-run or a human edit. S2: its ID lists come from the raw source,
    // so a caller who may not read raw sources is refused, as for the source itself.
    public async Task<SourceIdCoverage?> GetSourceIdCoverageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        if (!_currentUser.CanReadRawSources())
        {
            throw new UnauthorizedAccessException("Requirement source coverage is not available to your role; use the approved requirements.");
        }

        return await SourceIdCoverage.ForSourceAsync(_db, source, cancellationToken);
    }

    private static RequirementSourceResponse ToResponse(RequirementSource source, RequirementSourceFile? file = null) => new(
        source.Id, source.ProjectId, source.SourceType, source.RawText, source.OriginalFileName, source.CreatedAt,
        file is null ? null : new OriginalDocumentInfo(file.Format, file.Length, PageLineStarts(file), file.CreatedAt));

    private static IReadOnlyList<int>? PageLineStarts(RequirementSourceFile file)
    {
        if (file.PageLineStartsJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<int>>(file.PageLineStartsJson);
        }
        catch (JsonException)
        {
            return null; // the viewer then opens the PDF at its first page and says so
        }
    }
}
