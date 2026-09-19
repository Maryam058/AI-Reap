using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §19 — transforms a requirement source's functional requirements (plus design/API/DB
// artifacts, when present) into implementation tasks, linked to the FRs they realize via
// ArtifactRelationship(Implements).
public interface IImplementationPlanningService
{
    Task<IReadOnlyList<ArtifactResponse>> GenerateAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);
}
