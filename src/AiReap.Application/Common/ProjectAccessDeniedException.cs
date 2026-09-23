namespace AiReap.Application.Common;

// Thrown by IProjectAccessService.EnsureMemberAsync. Mapped to 403 by
// AiReap.Api.ErrorHandling.ProjectAccessDeniedExceptionHandler.
public sealed class ProjectAccessDeniedException : Exception
{
    public ProjectAccessDeniedException(Guid projectId) : base($"You are not a member of project {projectId}.")
    {
    }
}
