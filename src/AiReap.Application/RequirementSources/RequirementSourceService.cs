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

        return sources.Select(ToResponse).ToList();
    }

    public async Task<RequirementSourceResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.RequirementSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return null;
        }

        await _projectAccess.EnsureMemberAsync(source.ProjectId, cancellationToken);
        return ToResponse(source);
    }

    private static RequirementSourceResponse ToResponse(RequirementSource source) => new(
        source.Id, source.ProjectId, source.SourceType, source.RawText, source.OriginalFileName, source.CreatedAt);
}
