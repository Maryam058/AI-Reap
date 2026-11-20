namespace AiReap.Application.Common;

// Roles of other users (ICurrentUser only knows the caller's). Implemented in Infrastructure over
// ASP.NET Identity, so Application services stay independent of it. Deactivated users are left out:
// they can't act, so they can't count as a stage's owner either.
public interface IUserRoleDirectory
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetActiveUserRolesAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);

    // Ids of every active user holding the role (e.g. all Administrators, for notification fallback).
    Task<IReadOnlyList<string>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default);
}
