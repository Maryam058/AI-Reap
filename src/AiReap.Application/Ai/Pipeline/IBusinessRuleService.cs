using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §11 — extracts business rules from a requirement source's functional requirements and
// links each rule to the requirement(s) it constrains via ArtifactRelationship(LinkedRule).
public interface IBusinessRuleService
{
    Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
