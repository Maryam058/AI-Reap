namespace AiReap.Application.Ai.Pipeline;

// §15 — scans a project's functional requirements for duplicates/near-duplicates/
// contradictions/overlaps. Always "Human Resolution Required" — never auto-resolved.
public interface IConflictDetectionService
{
    Task<IReadOnlyList<ConflictFinding>> DetectAsync(Guid projectId, CancellationToken cancellationToken = default);
}
