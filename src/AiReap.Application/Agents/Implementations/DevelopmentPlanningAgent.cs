using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;

namespace AiReap.Application.Agents.Implementations;

// Stage 4 — turns requirements + design into a work plan. Plans work; never writes or ships code.
public class DevelopmentPlanningAgent : IAgent
{
    private readonly IImplementationPlanningService _planning;
    private readonly IAiReapDbContext _db;

    public DevelopmentPlanningAgent(IImplementationPlanningService planning, IAiReapDbContext db)
    {
        _planning = planning;
        _db = db;
    }

    public AgentKind Kind => AgentKind.DevelopmentPlanning;
    public string DisplayName => "Development Planning Agent";
    public string Responsibility => "Breaks the functional requirements and design into implementation tasks with types, priorities, dependencies and estimates.";
    public string Inputs => "Functional requirements plus design, data entity and API artifacts.";
    public string Outputs => "ImplementationTask artifacts linked to the requirements they implement.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.Developer);

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var sourceId = context.RequirementSourceId;
        var produced = new List<AgentArtifactRef>();

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.ImplementationTask, cancellationToken))
        {
            produced.AddRange((await _planning.GenerateAsync(sourceId, cancellationToken)).Select(AgentSupport.ToRef));
        }

        var total = (await AgentSupport.LoadAsync(_db, sourceId, ArtifactType.ImplementationTask, cancellationToken)).Count;
        return new AgentOutput(
            produced.Count > 0 ? $"Planned {produced.Count} implementation task(s)." : $"Reused {total} existing implementation task(s).",
            new Dictionary<string, int> { ["ImplementationTask"] = total },
            produced,
            Array.Empty<string>(),
            "Review task scope, dependencies and estimates. Approving a task is what later promotes its requirement to Implemented — this platform does not build or deploy anything.");
    }
}
