using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

public record GenerateRequirementsResult(
    IReadOnlyList<ArtifactResponse> FunctionalRequirements,
    IReadOnlyList<ArtifactResponse> NonFunctionalRequirements);
