using AiReap.Application.Ai;
using AiReap.Application.Common;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.RequirementSources;

public class RequirementSourceService : IRequirementSourceService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public RequirementSourceService(IAiReapDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
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

    public async Task<IReadOnlyList<RequirementSourceResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var sources = await _db.RequirementSources
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        // S2 - the workspace groups artifacts by source, so a caller who may not read raw sources
        // still gets each source's id, type and date, but never its text or file name.
        return _currentUser.CanReadRawSources()
            ? sources.Select(ToResponse).ToList()
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

        return ToResponse(source);
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

    private static RequirementSourceResponse ToResponse(RequirementSource source) => new(
        source.Id, source.ProjectId, source.SourceType, source.RawText, source.OriginalFileName, source.CreatedAt);
}
