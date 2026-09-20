namespace AiReap.Application.Agents;

// §36/REAP-090/091 — runs the agent chain one stage at a time, pausing for a human decision
// after every stage. Nothing here deploys or executes generated code (REAP-092).
public interface IAgentOrchestrator
{
    IReadOnlyList<AgentDefinitionResponse> GetDefinitions();

    // Throws KeyNotFoundException (unknown source), InvalidOperationException (a run for that
    // source is already active).
    Task<AgentRunResponse> StartAsync(Guid requirementSourceId, CancellationToken cancellationToken = default);

    Task<AgentRunResponse?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRunResponse>> GetForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    // Approve advances to the next stage (running it); reject halts the run. Throws
    // KeyNotFoundException, InvalidOperationException (run not awaiting a decision),
    // UnauthorizedAccessException (caller's role may not decide this stage).
    Task<AgentRunResponse> DecideAsync(Guid runId, bool approve, string? comment, CancellationToken cancellationToken = default);

    // Re-runs the failed stage. Same exceptions as DecideAsync for state/authorization.
    Task<AgentRunResponse> RetryAsync(Guid runId, CancellationToken cancellationToken = default);
}
