using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;

namespace AiReap.Application.Common;

// §4 - "Developer: view approved requirements, designs, stories and implementation tasks." A caller
// whose only artifact-reading role is Developer sees a requirement-side artifact only once it is
// approved or later (ArtifactStatusTransitions.IsApprovedOrLater). §17 - generated designs need the
// Developer's technical review before they are approved, so technical artifacts (TechnicalTypes) are
// visible to a Developer in every status. Every other role keeps full visibility. Applied in the
// Application services on top of project membership, so every API route that returns artifact data
// gets the same rule. A hidden artifact is treated exactly like one that doesn't exist.
public static class ArtifactVisibility
{
    private static readonly string[] FullVisibilityRoles = Roles.All.Where(r => r != Roles.Developer).ToArray();

    // Exposed as a collection so EF queries can filter on it (Contains translates to IN).
    public static readonly IReadOnlyList<ArtifactType> TechnicalTypes =
        [ArtifactType.DesignArtifact, ArtifactType.ApiSpecification, ArtifactType.DataEntity, ArtifactType.ImplementationTask];

    public static bool SeesOnlyApprovedArtifacts(this ICurrentUser user) =>
        user.IsInRole(Roles.Developer) && !FullVisibilityRoles.Any(user.IsInRole);

    // S2 - raw requirement sources (pasted or uploaded text) and project documents are unreviewed
    // input. The same restricted caller works from the structured artifacts and never reads that input
    // or anything quoted from it (file names, excerpts, analysis of it). Other roles keep access.
    public static bool CanReadRawSources(this ICurrentUser user) => !user.SeesOnlyApprovedArtifacts();

    public static bool CanSee(this ICurrentUser user, Artifact artifact) =>
        user.CanSee(artifact.ArtifactType, artifact.Status);

    public static bool CanSee(this ICurrentUser user, ArtifactType type, ArtifactStatus status) =>
        !user.SeesOnlyApprovedArtifacts() || TechnicalTypes.Contains(type) || ArtifactStatusTransitions.IsApprovedOrLater(status);

    public static IQueryable<Artifact> VisibleTo(this IQueryable<Artifact> artifacts, ICurrentUser user) =>
        user.SeesOnlyApprovedArtifacts()
            ? artifacts.Where(a => TechnicalTypes.Contains(a.ArtifactType) || ArtifactStatusTransitions.ApprovedOrLaterStatuses.Contains(a.Status))
            : artifacts;
}
