using AiReap.Domain.Enums;

namespace AiReap.Application.Agents;

// §36 — the structured input/output contract every agent honours. Agents never talk to each
// other directly: each reads the persisted artifacts earlier stages left behind (that is the
// hand-off), and reports what it did in an AgentOutput a human reviews before the next stage.

public record AgentContext(Guid ProjectId, Guid RequirementSourceId);

public record AgentArtifactRef(Guid Id, string Code, string Type, string Title);

public record AgentOutput(
    string Summary,
    IReadOnlyDictionary<string, int> Metrics,
    IReadOnlyList<AgentArtifactRef> ProducedArtifacts,
    IReadOnlyList<string> Findings,
    string ReviewGuidance);

public interface IAgent
{
    AgentKind Kind { get; }
    string DisplayName { get; }
    string Responsibility { get; }
    string Inputs { get; }
    string Outputs { get; }

    // Roles allowed to approve this agent's stage (REAP-091). Administrator is always included.
    IReadOnlyList<string> ApproverRoles { get; }

    // Must be safe to re-run after a partial failure or in a later run against the same source:
    // sub-steps whose artifacts already exist are reused, never duplicated.
    Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken);
}
