namespace AiReap.Domain.Entities;

// §38 DoD — "auth/authz across multiple projects": membership is what gates whether a user can
// reach a project's data at all, on top of the existing role gates (WriterRoles/ReviewerRoles
// etc.) that decide what they're allowed to do with it once they're in. Deliberately does not
// carry a "role on this project" - the app has only one role vocabulary (the five global roles
// already on the user's account), and duplicating it here would just be a second, driftable copy
// of the same thing for no enforcement benefit; see IProjectAccessService.
public class ProjectMember
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string AddedByUserId { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}
