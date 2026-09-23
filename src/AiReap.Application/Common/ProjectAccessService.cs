using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Common;

public class ProjectAccessService : IProjectAccessService
{
    private readonly IAiReapDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ProjectAccessService(IAiReapDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (_currentUser.IsInRole(Roles.Administrator))
        {
            return true;
        }

        var userId = _currentUser.UserId;
        return await _db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, cancellationToken);
    }

    public async Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!await IsMemberAsync(projectId, cancellationToken))
        {
            throw new ProjectAccessDeniedException(projectId);
        }
    }
}
