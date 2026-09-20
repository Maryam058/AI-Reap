using AiReap.Domain.Entities;

namespace AiReap.Application.Artifacts;

public static class ArtifactResponseMapper
{
    public static ArtifactResponse ToResponse(Artifact artifact) => new(
        artifact.Id,
        artifact.ProjectId,
        artifact.ArtifactType,
        artifact.Code,
        artifact.Title,
        artifact.Priority,
        artifact.Status,
        artifact.Origin,
        artifact.RequirementSourceId,
        ArtifactJson.ToCamelCaseElement(artifact.DataJson),
        artifact.CurrentVersion,
        artifact.CreatedAt,
        artifact.UpdatedAt);
}
