using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

// §20 — generates test cases for a specific functional requirement, linked back via
// ArtifactRelationship(TestedBy): Source = the FR, Target = the generated TestCase.
public interface ITestGenerationService
{
    // For a functional or non-functional requirement; null when the id is neither.
    Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid requirementArtifactId, CancellationToken cancellationToken = default);
}
