namespace AiReap.Application.Knowledge;

// §25 — a read-only, project-scoped Q&A layer over (a) the same structured data Traceability
// and Impact Analysis use and (b) semantic search over uploaded documents (§26). Never persists
// an artifact - like RequirementQualityService/ConflictDetectionService, its output is an
// ephemeral answer, only the AIExecution call is logged for the audit trail.
public interface ICopilotService
{
    Task<CopilotAnswerResponse> AskAsync(Guid projectId, string question, CancellationToken cancellationToken = default);
}
