using AiReap.Application.Common;
using AiReap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Infrastructure.Identity;

public class UserRoleDirectory : IUserRoleDirectory
{
    private readonly AiReapDbContext _db;

    public UserRoleDirectory(AiReapDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetActiveUserRolesAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }

        // Deactivation is an open-ended Identity lockout (UsersController.SetStatus).
        var now = DateTimeOffset.UtcNow;
        var rows = await (
            from u in _db.Users
            where ids.Contains(u.Id) && (u.LockoutEnd == null || u.LockoutEnd <= now)
            join ur in _db.UserRoles on u.Id equals ur.UserId
            join r in _db.Roles on ur.RoleId equals r.Id
            select new { u.Id, r.Name })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name!).ToList());
    }

    public async Task<IReadOnlyList<string>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await (
            from r in _db.Roles
            where r.Name == role
            join ur in _db.UserRoles on r.Id equals ur.RoleId
            join u in _db.Users on ur.UserId equals u.Id
            where u.LockoutEnd == null || u.LockoutEnd <= now
            select u.Id)
            .ToListAsync(cancellationToken);
    }
}
