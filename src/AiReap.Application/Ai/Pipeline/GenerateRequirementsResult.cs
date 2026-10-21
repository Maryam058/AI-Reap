using AiReap.Application.Artifacts;

namespace AiReap.Application.Ai.Pipeline;

public record GenerateRequirementsResult(
    IReadOnlyList<ArtifactResponse> FunctionalRequirements,
    IReadOnlyList<ArtifactResponse> NonFunctionalRequirements,
    // Generated requirements not saved because the same requirement already exists for this source
    // (ArtifactFingerprints): a retry or a re-run never duplicates.
    int SkippedDuplicates = 0,
    // P0-4 — the source's requirement IDs no saved, non-rejected requirement cites, counted after
    // this save (so including requirements saved earlier). Also GET .../source-id-coverage.
    SourceIdCoverage? SourceIdCoverage = null,
    // P0-7 — whole or split, clarifications, models, and the evidence counts of the requirements this
    // run created. Response-only; coverage stays in SourceIdCoverage.
    RequirementCompleteness? Completeness = null);
