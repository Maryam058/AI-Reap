using System.Text.Json;
using AiReap.Api.Startup;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiReap.Tests;

// The product rules settled for the demo release, offline (InMemoryAiReapDb, scripted AI, no provider):
//   1. Verification is not approval: a passed test never approves anything, and an NFR's status is
//      never changed by its tests' outcomes.
//   2. No downstream generation from a source with no valid FR: refused before any AI call.
//   4. The startup warm-up is on by default, limited, and configurable.
public class ReleaseRuleTests
{
    private sealed class QaUser : ICurrentUser
    {
        public string UserId => "qa-user";
        public bool IsInRole(string role) => role == Roles.QA;
    }

    private readonly InMemoryAiReapDb _db = new();
    private readonly RequirementSource _source;

    public ReleaseRuleTests()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Release rules", CreatedByUserId = "u" };
        _source = new RequirementSource
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = RequirementSourceType.Paste, RawText = "The portal shall let patients book.",
            CreatedByUserId = "u", CreatedAt = DateTime.UtcNow
        };
        _db.Projects.Add(project);
        _db.RequirementSources.Add(_source);
    }

    private Artifact Add(ArtifactType type, string code, ArtifactStatus status, Guid? sourceId = null)
    {
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = _source.ProjectId, RequirementSourceId = sourceId ?? _source.Id, ArtifactType = type, Code = code,
            Title = code, Status = status, Origin = ArtifactOrigin.Ai, DataJson = "{}", CurrentVersion = 1, CreatedByUserId = "u",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Artifacts.Add(artifact);
        return artifact;
    }

    private void TestedBy(Artifact requirement, Artifact test) => _db.ArtifactRelationships.Add(new ArtifactRelationship
    {
        Id = Guid.NewGuid(), SourceArtifactId = requirement.Id, TargetArtifactId = test.Id, RelationshipType = RelationshipType.TestedBy, CreatedAt = DateTime.UtcNow
    });

    private Task Pass(Artifact test) =>
        new ArtifactWorkService(_db, new QaUser(), new AllowAllProjectAccess()).RecordAsync(test.Id, new RecordWorkRequest(WorkOutcome.TestPassed, null));

    // ---- 1. verification is not approval ---------------------------------------------------------

    [Fact]
    public async Task Passing_every_test_of_an_nfr_leaves_its_status_and_approval_unchanged()
    {
        var nfr = Add(ArtifactType.NonFunctionalRequirement, "NFR-001", ArtifactStatus.UnderReview);
        var test = Add(ArtifactType.TestCase, "TC-001", ArtifactStatus.Approved);
        TestedBy(nfr, test);

        await Pass(test);

        Assert.Equal(ArtifactStatus.UnderReview, nfr.Status);
        Assert.Single(InMemoryAiReapDb.Rows(_db.ArtifactWorkRecords)); // the verification evidence is recorded
        Assert.Empty(InMemoryAiReapDb.Rows(_db.ArtifactReviews));      // and is not an approval
    }

    [Fact]
    public async Task Passing_tests_never_approve_a_functional_requirement_that_is_not_approved()
    {
        var fr = Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.UnderReview);
        var test = Add(ArtifactType.TestCase, "TC-001", ArtifactStatus.Approved);
        TestedBy(fr, test);

        await Pass(test);

        Assert.Equal(ArtifactStatus.UnderReview, fr.Status);
    }

    [Fact]
    public void Only_an_approved_requirement_can_move_on_to_implemented_and_verified()
    {
        Assert.False(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.UnderReview, ArtifactStatus.Verified));
        Assert.False(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.AiGenerated, ArtifactStatus.Approved));
        Assert.False(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.Approved, ArtifactStatus.Verified)); // implemented first
        Assert.True(ArtifactStatusTransitions.CanSystemPromote(ArtifactStatus.Implemented, ArtifactStatus.Verified));
    }

    // ---- 2. no downstream generation without a valid FR ---------------------------------------------

    private static readonly OfflineCurrentUser Ba = new();
    private static readonly AllowAllProjectAccess Access = new();

    public static TheoryData<string> DownstreamStages() => ["stories", "rules", "design", "entities", "apis", "tasks"];

    private Task Run(string stage, ScriptedAiChatClient ai) => stage switch
    {
        "stories" => new UserStoryService(ai, _db, Ba, Access, NullLogger<UserStoryService>.Instance).GenerateStoriesAsync(_source.Id),
        "rules" => new BusinessRuleService(ai, _db, Ba, Access, NullLogger<BusinessRuleService>.Instance).GenerateAsync(_source.Id),
        "design" => new SolutionDesignService(ai, _db, Ba, Access, NullLogger<SolutionDesignService>.Instance).GenerateAsync(_source.Id),
        "entities" => new DatabaseDesignService(ai, _db, Ba, Access, NullLogger<DatabaseDesignService>.Instance).GenerateAsync(_source.Id),
        "apis" => new ApiDesignService(ai, _db, Ba, Access, NullLogger<ApiDesignService>.Instance).GenerateAsync(_source.Id),
        "tasks" => new ImplementationPlanningService(ai, _db, Ba, Access, NullLogger<ImplementationPlanningService>.Instance).GenerateAsync(_source.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    [Theory]
    [MemberData(nameof(DownstreamStages))]
    public async Task A_source_with_no_functional_requirement_is_refused_before_any_ai_call(string stage)
    {
        Add(ArtifactType.NonFunctionalRequirement, "NFR-001", ArtifactStatus.Approved); // an NFR alone is not enough
        Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Approved, sourceId: Guid.NewGuid()); // another source's FR
        var ai = new ScriptedAiChatClient();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Run(stage, ai));

        Assert.Contains("no functional requirements yet", ex.Message);
        Assert.Empty(ai.Prompts);
        Assert.Equal(2, InMemoryAiReapDb.Rows(_db.Artifacts).Count());
    }

    [Theory]
    [MemberData(nameof(DownstreamStages))]
    public async Task A_source_whose_functional_requirements_are_all_rejected_keeps_its_own_refusal(string stage)
    {
        Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Rejected);
        var ai = new ScriptedAiChatClient();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Run(stage, ai));

        Assert.Contains("has been rejected", ex.Message);
        Assert.Empty(ai.Prompts);
    }

    // ---- 4. warm-up: on by default, limited -------------------------------------------------------

    [Fact]
    public void The_warm_up_is_on_by_default_with_limits_and_each_limit_can_be_configured()
    {
        var defaults = ClarificationWarmup.Read(new ConfigurationBuilder().Build());
        Assert.Equal(new ClarificationWarmup.Settings(true, TimeSpan.FromSeconds(5), 200, TimeSpan.FromSeconds(300)), defaults);

        var configured = ClarificationWarmup.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClarificationVisibility:WarmOnStartup"] = "false",
            ["ClarificationVisibility:WarmStartDelaySeconds"] = "30",
            ["ClarificationVisibility:WarmMaxSources"] = "10",
            ["ClarificationVisibility:WarmMaxSeconds"] = "60",
        }).Build());
        Assert.Equal(new ClarificationWarmup.Settings(false, TimeSpan.FromSeconds(30), 10, TimeSpan.FromSeconds(60)), configured);
    }

    [Fact]
    public async Task The_warm_up_takes_the_most_recently_changed_sources_first_and_stops_at_its_limit()
    {
        var older = new RequirementSource { Id = Guid.NewGuid(), ProjectId = _source.ProjectId, RawText = "Old.", CreatedByUserId = "u" };
        _db.RequirementSources.Add(older);
        var recent = Add(ArtifactType.ClarificationQuestion, "CQ-001", ArtifactStatus.AiGenerated);
        recent.DataJson = JsonSerializer.Serialize(new { Question = "Q?", ClarificationStatus = "Open" });
        var stale = Add(ArtifactType.ClarificationQuestion, "CQ-002", ArtifactStatus.AiGenerated, sourceId: older.Id);
        stale.DataJson = recent.DataJson;
        stale.UpdatedAt = DateTime.UtcNow.AddDays(-30);

        Assert.Equal(1, await ClarificationVisibility.WarmAsync(_db, CancellationToken.None, maxSources: 1));
        Assert.True(ClarificationVisibility.Results.TryGet(recent, ClarificationVisibility.Results.SourceGeneration(_source.Id, _source.RawText), out _));
        Assert.False(ClarificationVisibility.Results.TryGet(stale, ClarificationVisibility.Results.SourceGeneration(older.Id, older.RawText), out _));
        Assert.Equal(0, await ClarificationVisibility.WarmAsync(_db, CancellationToken.None, budget: TimeSpan.Zero));
    }
}
