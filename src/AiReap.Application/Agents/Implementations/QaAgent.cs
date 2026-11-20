using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;

namespace AiReap.Application.Agents.Implementations;

// Stage 5 — test coverage. Generates test cases for functional and non-functional requirements that have none.
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
    public string Responsibility => "Generates positive, negative, boundary, permission and validation test cases for each functional requirement, and quality test cases (performance, security, availability...) for each non-functional requirement.";
    public string Inputs => "Functional and non-functional requirements.";
    public string Outputs => "TestCase artifacts linked (TestedBy) to their requirements.";
    public IReadOnlyList<string> ApproverRoles => AgentSupport.AdminAnd(Roles.QA);
    public IReadOnlyList<ProjectStatus> LifecycleStages => [ProjectStatus.Testing];
    public string LifecycleContribution => "Drafts test cases for Testing. Verification evidence comes from QA recording outcomes, not from the drafts.";

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

        // Non-functional requirements get test cases of their own (performance, security, availability...),
        // chosen the same way: not rejected, none generated yet.
        var nfrs = await AgentSupport.LoadAsync(_db, sourceId, ArtifactType.NonFunctionalRequirement, cancellationToken);

        var produced = new List<AgentArtifactRef>();
        foreach (var requirement in SelectForTestGeneration(frs, alreadyTested).Concat(SelectForTestGeneration(nfrs, alreadyTested)))
        {
            var cases = await _tests.GenerateAsync(requirement.Id, cancellationToken);
            if (cases is not null)
            {
                produced.AddRange(cases.Select(AgentSupport.ToRef));
            }
        }

        var total = (await AgentSupport.LoadAsync(_db, sourceId, ArtifactType.TestCase, cancellationToken)).Count;
        return new AgentOutput(
            $"Generated {produced.Count} new test case(s); {frs.Count} functional and {nfrs.Count} non-functional requirement(s) in scope.",
            new Dictionary<string, int> { ["FunctionalRequirement"] = frs.Count, ["NonFunctionalRequirement"] = nfrs.Count, ["TestCase"] = total },
            produced,
            Array.Empty<string>(),
            "Review test cases for coverage and correctness. Approving a test case accepts it; its requirement becomes Verified only when QA records every linked test case as passed (after its tasks are complete).");
    }

    // The FRs to generate test cases for: those without any yet, leaving out rejected ones (GenerationInputs).
    public static IEnumerable<Artifact> SelectForTestGeneration(IEnumerable<Artifact> frs, IReadOnlySet<Guid> alreadyTested) =>
        frs.Where(f => GenerationInputs.IsEligible(f) && !alreadyTested.Contains(f.Id));
}
