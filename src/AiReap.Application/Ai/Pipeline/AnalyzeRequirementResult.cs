using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

public record AnalyzeRequirementResult(
    IReadOnlyList<string> Actors,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> DataElements,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ArtifactResponse> ClarificationQuestions,
    // Questions not saved because the same question already exists for this source.
    int SkippedDuplicates = 0,
    // P0-7 — whether the source was sent whole or in parts, and which models served it. Response-only.
    RequirementCompleteness? Completeness = null);
