namespace AiReap.Application.Ai.Pipeline;

// §14 — an ephemeral analysis result, not a persisted artifact (there's no "quality finding"
// artifact type in the model — see ADR-002). Logged via AIExecution for audit only.
public record QualityFinding(Guid ArtifactId, string ArtifactCode, string ArtifactTitle, string Issue, string Recommendation);
