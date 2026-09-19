using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §20 — generates test cases for a specific functional requirement, linked back via
// ArtifactRelationship(TestedBy): Source = the FR, Target = the generated TestCase.
public interface ITestGenerationService
{
    Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid functionalRequirementArtifactId, CancellationToken cancellationToken = default);
}
