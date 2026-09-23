namespace AiReap.Application.Audit;

// §27 — deliberately no chain-of-thought field; only structured application inputs/outputs.
// PromptTemplateVersion is a per-service constant next to that service's system prompt (e.g.
// "RequirementAnalysis-v1"), bumped by whoever next edits that prompt - not a separately
// versioned template store, but enough to tell which prompt revision produced a given output.
// ProducedArtifactId is populated per execution row (GenerationSupport.AddExecutions logs one
// row per produced artifact), and Accepted is set when a human later approves/rejects that
// artifact (ArtifactService.UpdateStatusAsync). Both are null for calls that produce no artifact
// (quality analysis, conflict detection, copilot queries, the review agent's narrative).
public record AiExecutionResponse(
    Guid Id,
    string OperationType,
    string UserId,
    DateTime Timestamp,
    string Model,
    string? PromptTemplateVersion,
    string? InputReference,
    string OutputJson,
    bool? Accepted,
    Guid? ProducedArtifactId);
