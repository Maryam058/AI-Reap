using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

public record AnalyzeRequirementResult(
    IReadOnlyList<string> Actors,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> DataElements,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ArtifactResponse> ClarificationQuestions);
