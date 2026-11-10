using System.Text.Json;
using AiReap.Application.Agents;
using AiReap.Application.Agents.Implementations;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Traceability;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// §20 — non-functional requirements get test cases of their own: an NFR-specific prompt (category,
// target and whether a person confirmed it, evidence), quality test kinds, and the same TestedBy link,
// deduplication and audit as FR test cases. Offline: InMemoryAiReapDb, scripted AI (no provider call).
public class NfrTestGenerationTests
{
    private readonly InMemoryAiReapDb _db = new();
    private readonly RequirementSource _source;

    public NfrTestGenerationTests()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "NFR tests", CreatedByUserId = "u" };
        _source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste,
            RawText = "NFR-01 Search results shall be returned within 2 seconds.", CreatedByUserId = "u", CreatedAt = DateTime.UtcNow
        };
        _db.Projects.Add(project);
        _db.RequirementSources.Add(_source);
    }

    private Artifact Nfr(string code, string? target, string? assumption, ArtifactStatus status = ArtifactStatus.Approved)
    {
        var nfr = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = _source.ProjectId, RequirementSourceId = _source.Id, ArtifactType = ArtifactType.NonFunctionalRequirement,
            Code = code, Title = "Search responds quickly", Status = status, Origin = ArtifactOrigin.Ai, CurrentVersion = 1, CreatedByUserId = "u",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(new NonFunctionalRequirementPayload
            {
                Category = "Performance", Description = "Search results are returned quickly.", TargetValue = target, AssumptionStatus = assumption
            })
        };
        _db.Artifacts.Add(nfr);
        return nfr;
    }

    private static string Response(params (string Title, string Kind)[] cases) => JsonSerializer.Serialize(new
    {
        testCases = cases.Select(c => new
        {
            title = c.Title, preconditions = "10,000 products indexed", steps = new[] { $"Run: {c.Title}" },
            expectedResult = "Results meet the agreed target", testKind = c.Kind
        })
    });

    private TestGenerationService Service(ScriptedAiChatClient ai) =>
        new(ai, _db, new OfflineCurrentUser(), new AllowAllProjectAccess(), NullLogger<TestGenerationService>.Instance);

    [Fact]
    public async Task An_nfr_gets_quality_test_cases_linked_to_it_and_audited()
    {
        var nfr = Nfr("NFR-001", "2 seconds", "confirmed");
        var ai = new ScriptedAiChatClient(Response(("Search under peak load", "performance"), ("Search with an expired session", "security")));

        var created = Assert.IsType<GeneratedArtifacts>(await Service(ai).GenerateAsync(nfr.Id));

        Assert.Equal(2, created.Count);
        var tests = InMemoryAiReapDb.Rows(_db.Artifacts).Where(a => a.ArtifactType == ArtifactType.TestCase).ToList();
        Assert.All(tests, t => Assert.Contains(InMemoryAiReapDb.Rows(_db.ArtifactRelationships),
            r => r.SourceArtifactId == nfr.Id && r.TargetArtifactId == t.Id && r.RelationshipType == RelationshipType.TestedBy));
        Assert.Equal(["performance", "security"], tests.Select(t => JsonDocument.Parse(t.DataJson).RootElement.GetProperty("TestKind").GetString()).Order());
        Assert.All(InMemoryAiReapDb.Rows(_db.AIExecutions), e => Assert.Equal("NfrTestCaseGeneration-v1", e.PromptTemplateVersion));

        var prompt = Assert.Single(ai.Prompts);
        Assert.Contains("non-functional requirement", prompt.System);
        Assert.Contains("Category: Performance", prompt.User);
        Assert.Contains("Target value: 2 seconds", prompt.User);
        Assert.Contains("Target status: confirmed", prompt.User);
    }

    // The development stub (no API key) answers an NFR prompt with quality test kinds and an FR prompt
    // with functional ones, so a demo without a key shows NFR test cases as they are meant to look.
    [Fact]
    public async Task The_development_stub_gives_an_nfr_quality_test_kinds_and_an_fr_functional_ones()
    {
        var nfr = Nfr("NFR-001", "2 seconds", "proposed_assumption");
        var fr = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = _source.ProjectId, RequirementSourceId = _source.Id, ArtifactType = ArtifactType.FunctionalRequirement,
            Code = "FR-001", Title = "Search products", Status = ArtifactStatus.Approved, Origin = ArtifactOrigin.Ai, CurrentVersion = 1,
            CreatedByUserId = "u", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(new FunctionalRequirementPayload { Actor = "Customer", ExpectedResult = "Results are listed" })
        };
        _db.Artifacts.Add(fr);
        var stub = new AiReap.Infrastructure.Ai.StubAiChatClient();

        await new TestGenerationService(stub, _db, new OfflineCurrentUser(), new AllowAllProjectAccess(), NullLogger<TestGenerationService>.Instance).GenerateAsync(nfr.Id);
        await new TestGenerationService(stub, _db, new OfflineCurrentUser(), new AllowAllProjectAccess(), NullLogger<TestGenerationService>.Instance).GenerateAsync(fr.Id);

        string KindsOf(Artifact requirement) => string.Join(",", InMemoryAiReapDb.Rows(_db.ArtifactRelationships)
            .Where(r => r.SourceArtifactId == requirement.Id && r.RelationshipType == RelationshipType.TestedBy)
            .Select(r => InMemoryAiReapDb.Rows(_db.Artifacts).Single(a => a.Id == r.TargetArtifactId))
            .Select(t => JsonDocument.Parse(t.DataJson).RootElement.GetProperty("TestKind").GetString())
            .Order());
        Assert.Equal("performance,security", KindsOf(nfr));
        Assert.Equal("positive,validation", KindsOf(fr));
    }

    [Fact]
    public async Task An_unconfirmed_target_is_presented_as_an_assumption_not_an_agreed_limit()
    {
        var nfr = Nfr("NFR-001", "2 seconds", "proposed_assumption");
        var ai = new ScriptedAiChatClient(Response(("Search under peak load", "performance")));

        await Service(ai).GenerateAsync(nfr.Id);

        Assert.Contains("Target status: proposed assumption, not confirmed", Assert.Single(ai.Prompts).User);
    }

    [Fact]
    public async Task Running_it_again_with_the_same_output_saves_nothing_twice()
    {
        var nfr = Nfr("NFR-001", null, null);
        var response = Response(("Search under peak load", "load"));

        await Service(new ScriptedAiChatClient(response)).GenerateAsync(nfr.Id);
        var again = Assert.IsType<GeneratedArtifacts>(await Service(new ScriptedAiChatClient(response)).GenerateAsync(nfr.Id));

        Assert.Equal((0, 1), (again.Count, again.SkippedDuplicates));
    }

    [Fact]
    public async Task A_rejected_nfr_is_refused_before_any_ai_call()
    {
        var nfr = Nfr("NFR-001", null, null, ArtifactStatus.Rejected);
        var ai = new ScriptedAiChatClient();

        await Assert.ThrowsAsync<AiReap.Application.Common.ConflictException>(() => Service(ai).GenerateAsync(nfr.Id));
        Assert.Empty(ai.Prompts);
    }

    [Fact]
    public async Task Nfr_test_cases_count_as_tracing_the_nfr_and_are_listed_in_its_coverage()
    {
        var nfr = Nfr("NFR-001", "2 seconds", "confirmed");
        await Service(new ScriptedAiChatClient(Response(("Search under peak load", "performance")))).GenerateAsync(nfr.Id);

        var gaps = await new TraceabilityService(_db, new AllowAllProjectAccess(), new OfflineCurrentUser()).GetGapsAsync(_source.ProjectId);

        Assert.Empty(gaps.UntracedNonFunctionalRequirements);
        Assert.Empty(gaps.Orphans);
        var coverage = Assert.Single(gaps.NonFunctionalTestCoverage);
        Assert.Equal(("NFR-001", "Performance"), (coverage.Code, coverage.Category));
        Assert.Single(coverage.TestCases);
    }

    private sealed class RecordingTests : ITestGenerationService
    {
        public List<Guid> Requested { get; } = [];

        public Task<IReadOnlyList<ArtifactResponse>?> GenerateAsync(Guid requirementArtifactId, CancellationToken cancellationToken = default)
        {
            Requested.Add(requirementArtifactId);
            return Task.FromResult<IReadOnlyList<ArtifactResponse>?>([]);
        }
    }

    [Fact]
    public async Task The_qa_agent_also_generates_for_nfrs_that_are_not_rejected_and_have_no_tests()
    {
        _db.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = _source.ProjectId, RequirementSourceId = _source.Id, ArtifactType = ArtifactType.FunctionalRequirement,
            Code = "FR-001", Title = "Search products", Status = ArtifactStatus.Approved, Origin = ArtifactOrigin.Ai, DataJson = "{}", CurrentVersion = 1,
            CreatedByUserId = "u", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var open = Nfr("NFR-001", null, null);
        Nfr("NFR-002", null, null, ArtifactStatus.Rejected);
        var tests = new RecordingTests();

        var output = await new QaAgent(tests, _db).RunAsync(new AgentContext(_source.ProjectId, _source.Id), CancellationToken.None);

        Assert.Contains(open.Id, tests.Requested);
        Assert.Equal(2, tests.Requested.Count); // FR-001 and NFR-001; the rejected NFR is left out
        Assert.Equal(2, output.Metrics["NonFunctionalRequirement"]);
    }
}
