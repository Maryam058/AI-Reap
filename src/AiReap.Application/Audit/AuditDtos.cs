namespace AiReap.Application.Audit;

// §27 — deliberately no chain-of-thought field; only structured application inputs/outputs.
// PromptTemplateVersion is not yet populated (system prompts are inline C# constants, not a
// separately versioned template store) — a known gap, not silently pretended away.
public record AiExecutionResponse(
    Guid Id,
    string OperationType,
    string UserId,
    DateTime Timestamp,
    string Model,
    string? PromptTemplateVersion,
    string? InputReference,
    string OutputJson,
    bool? Accepted);
