namespace AiReap.Application.Common;

// §38 DoD — "auth/authz across multiple projects." Membership gates whether a user can reach a
// project's data at all, on top of the existing role gates (WriterRoles/ReviewerRoles etc.) that
// decide what they're allowed to do with it once they're in. Administrator bypasses membership
// entirely, consistent with Administrator already being included in every role gate in the app.
public interface IProjectAccessService
{
    Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Throws ProjectAccessDeniedException if the current user is neither Administrator nor a
    // member of this project. Called directly by Application services reached through a route
    // that only carries an artifact/source/run id (the project has to be resolved by loading
    // that entity first) - routes with a literal {projectId} segment are instead covered
    // globally by AiReap.Api.Authorization.ProjectMembershipFilter, with no per-service call
    // needed.
    Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default);
}
