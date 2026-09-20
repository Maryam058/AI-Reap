using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;

namespace AiReap.Application.Agents.Implementations;

// Stage 5 — test coverage. Generates test cases only for functional requirements that have none.
public class QaAgent : IAgent
{
    private readonly ITestGenerationService _tests;
    private readonly IAiReapDbContext _db;

    public QaAgent(ITestGenerationService tests, IAiReapDbContext db)
    {
        _tests = tests;
        _db = db;
    }

    public AgentKind Kind => AgentKind.QA;
    public string DisplayName => "QA Agent";
    public string Responsibility => "Generates positive, negative, boundary, permission and validation test cases for each functional requirement.";
    public string Inputs => "Functional requirements.";
    public string Outputs => "TestCase artifacts linked (TestedBy) to their requirements.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.QA);

    public async Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var sourceId = context.RequirementSourceId;
        var frs = await AgentSupport.LoadAsync(_db, sourceId, ArtifactType.FunctionalRequirement, cancellationToken);
        if (frs.Count == 0)
        {
            throw new InvalidOperationException("There are no functional requirements to test. Complete the Analysis stage first.");
        }

        var alreadyTested = await AgentSupport.SourcesOfAsync(
            _db, ArtifactType.TestCase, RelationshipType.TestedBy, context.ProjectId, cancellationToken);

        var produced = new List<AgentArtifactRef>();
        foreach (var fr in frs.Where(f => !alreadyTested.Contains(f.Id)))
        {
            var cases = await _tests.GenerateAsync(fr.Id, cancellationToken);
            if (cases is not null)
            {
                produced.AddRange(cases.Select(AgentSupport.ToRef));
            }
        }

        var total = (await AgentSupport.LoadAsync(_db, sourceId, ArtifactType.TestCase, cancellationToken)).Count;
        return new AgentOutput(
            $"Generated {produced.Count} new test case(s); {frs.Count} functional requirement(s) in scope.",
            new Dictionary<string, int> { ["FunctionalRequirement"] = frs.Count, ["TestCase"] = total },
            produced,
            Array.Empty<string>(),
            "Review test cases for coverage and correctness. Approving all of a requirement's test cases (after its tasks) is what promotes it to Verified.");
    }
}
