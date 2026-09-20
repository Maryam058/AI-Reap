using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;

namespace AiReap.Application.Agents.Implementations;

// Stage 3 — solution/data/API design recommendations. Advisory: nothing here is applied to code.
public class ArchitectureAgent : IAgent
{
    private readonly ISolutionDesignService _design;
    private readonly IDatabaseDesignService _database;
    private readonly IApiDesignService _api;
    private readonly IAiReapDbContext _db;

    public ArchitectureAgent(ISolutionDesignService design, IDatabaseDesignService database, IApiDesignService api, IAiReapDbContext db)
    {
        _design = design;
        _database = database;
        _api = api;
        _db = db;
    }

    public AgentKind Kind => AgentKind.Architecture;
    public string DisplayName => "Architecture Agent";
    public string Responsibility => "Proposes the solution architecture, data entities and API contracts for the requirements.";
    public string Inputs => "Functional requirements and clarifications from the Analysis stage.";
    public string Outputs => "DesignArtifact, DataEntity and ApiSpecification artifacts.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.Developer);

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var sourceId = context.RequirementSourceId;

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.FunctionalRequirement, cancellationToken))
        {
            throw new InvalidOperationException("There are no functional requirements to design against. Complete the Analysis stage first.");
        }

        var produced = new List<AgentArtifactRef>();

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.DesignArtifact, cancellationToken))
        {
            produced.Add(AgentSupport.ToRef(await _design.GenerateAsync(sourceId, cancellationToken)));
        }

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.DataEntity, cancellationToken))
        {
            produced.AddRange((await _database.GenerateAsync(sourceId, cancellationToken)).Select(AgentSupport.ToRef));
        }

        if (!await AgentSupport.ExistsAsync(_db, sourceId, ArtifactType.ApiSpecification, cancellationToken))
        {
            produced.AddRange((await _api.GenerateAsync(sourceId, cancellationToken)).Select(AgentSupport.ToRef));
        }

        var metrics = new Dictionary<string, int>();
        foreach (var type in new[] { ArtifactType.DesignArtifact, ArtifactType.DataEntity, ArtifactType.ApiSpecification })
        {
            metrics[type.ToString()] = (await AgentSupport.LoadAsync(_db, sourceId, type, cancellationToken)).Count;
        }

        return new AgentOutput(
            $"Produced {produced.Count} new design artifact(s).",
            metrics,
            produced,
            Array.Empty<string>(),
            "A developer must review the design, data entities and API contracts before task planning builds on them.");
    }
}
