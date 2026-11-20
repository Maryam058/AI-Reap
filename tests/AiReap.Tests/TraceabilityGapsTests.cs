using AiReap.Application.Common;
using AiReap.Domain.Entities;
using AiReap.Application.Traceability;
using AiReap.Domain.Enums;
using AiReap.Tests.SrsEval;
using Xunit;

namespace AiReap.Tests;

// §21 — the traceability gaps the FR-row matrix cannot show: downstream artifacts traced to no
// requirement (a generator whose cited codes did not resolve still saves them) and NFRs nothing traces
// to. Offline: InMemoryAiReapDb, no AI call.
public class TraceabilityGapsTests
{
    private readonly InMemoryAiReapDb _db = new();
    private readonly Guid _projectId = Guid.NewGuid();

    private Artifact Add(ArtifactType type, string code, ArtifactStatus status = ArtifactStatus.AiGenerated)
    {
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = _projectId, ArtifactType = type, Code = code, Title = code, Status = status,
            Origin = ArtifactOrigin.Ai, DataJson = "{}", CurrentVersion = 1, CreatedByUserId = "u",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Artifacts.Add(artifact);
        return artifact;
    }

    private void Link(Artifact from, Artifact to, RelationshipType type) => _db.ArtifactRelationships.Add(new ArtifactRelationship
    {
        Id = Guid.NewGuid(), SourceArtifactId = from.Id, TargetArtifactId = to.Id, RelationshipType = type, CreatedAt = DateTime.UtcNow
    });

    private Task<TraceabilityGapsResponse> GapsAsync(ICurrentUser? user = null) =>
        new TraceabilityService(_db, new AllowAllProjectAccess(), user ?? new OfflineCurrentUser()).GetGapsAsync(_projectId);

    [Fact]
    public async Task Downstream_artifacts_with_no_link_to_a_requirement_are_reported_and_linked_ones_are_not()
    {
        var fr = Add(ArtifactType.FunctionalRequirement, "FR-001");
        var tracedStory = Add(ArtifactType.UserStory, "US-001");
        Link(tracedStory, fr, RelationshipType.DerivedFrom);
        Link(Add(ArtifactType.AcceptanceCriterion, "AC-001"), tracedStory, RelationshipType.DerivedFrom);
        Link(fr, Add(ArtifactType.TestCase, "TC-001"), RelationshipType.TestedBy);
        Link(Add(ArtifactType.ImplementationTask, "TASK-001"), fr, RelationshipType.Implements);

        Add(ArtifactType.UserStory, "US-002");
        Add(ArtifactType.AcceptanceCriterion, "AC-002");
        Add(ArtifactType.BusinessRule, "BR-001");
        Add(ArtifactType.DataEntity, "DE-001");
        Add(ArtifactType.ApiSpecification, "API-001");
        Add(ArtifactType.ImplementationTask, "TASK-002");
        Add(ArtifactType.TestCase, "TC-002");
        Add(ArtifactType.UserStory, "US-003", ArtifactStatus.Rejected); // discarded on purpose: not a gap

        var gaps = await GapsAsync();

        Assert.Equal(["AC-002", "API-001", "BR-001", "DE-001", "TASK-002", "TC-002", "US-002"], gaps.Orphans.Select(o => o.Code).Order());
        Assert.Equal("user story", gaps.Orphans.Single(o => o.Code == "AC-002").MissingLink);
    }

    private TraceabilityService Service() => new(_db, new AllowAllProjectAccess(), new OfflineCurrentUser());

    [Fact]
    public async Task Linking_an_orphan_by_hand_uses_its_types_relationship_and_clears_the_gap()
    {
        var fr = Add(ArtifactType.FunctionalRequirement, "FR-001");
        var story = Add(ArtifactType.UserStory, "US-001");
        var criterion = Add(ArtifactType.AcceptanceCriterion, "AC-001");
        var test = Add(ArtifactType.TestCase, "TC-001");
        var rule = Add(ArtifactType.BusinessRule, "BR-001");

        await Service().LinkToRequirementAsync(story.Id, fr.Id);
        await Service().LinkToRequirementAsync(criterion.Id, story.Id);
        await Service().LinkToRequirementAsync(test.Id, fr.Id);
        await Service().LinkToRequirementAsync(rule.Id, fr.Id);
        await Service().LinkToRequirementAsync(rule.Id, fr.Id); // twice: one link

        var links = InMemoryAiReapDb.Rows(_db.ArtifactRelationships).ToList();
        Assert.Contains(links, r => r.SourceArtifactId == story.Id && r.TargetArtifactId == fr.Id && r.RelationshipType == RelationshipType.DerivedFrom);
        Assert.Contains(links, r => r.SourceArtifactId == criterion.Id && r.TargetArtifactId == story.Id && r.RelationshipType == RelationshipType.DerivedFrom);
        Assert.Contains(links, r => r.SourceArtifactId == fr.Id && r.TargetArtifactId == test.Id && r.RelationshipType == RelationshipType.TestedBy);
        Assert.Single(links, r => r.SourceArtifactId == rule.Id && r.RelationshipType == RelationshipType.LinkedRule);
        Assert.Empty((await GapsAsync()).Orphans);
    }

    [Fact]
    public async Task A_link_to_the_wrong_kind_of_artifact_or_a_rejected_one_is_refused_and_saves_nothing()
    {
        var story = Add(ArtifactType.UserStory, "US-001");
        var criterion = Add(ArtifactType.AcceptanceCriterion, "AC-001");
        var fr = Add(ArtifactType.FunctionalRequirement, "FR-001");
        var rejected = Add(ArtifactType.FunctionalRequirement, "FR-002", ArtifactStatus.Rejected);
        var objective = Add(ArtifactType.BusinessObjective, "BO-001");

        await Assert.ThrowsAsync<RequestValidationException>(() => Service().LinkToRequirementAsync(criterion.Id, fr.Id)); // AC traces to a story
        await Assert.ThrowsAsync<RequestValidationException>(() => Service().LinkToRequirementAsync(story.Id, objective.Id));
        await Assert.ThrowsAsync<ConflictException>(() => Service().LinkToRequirementAsync(story.Id, rejected.Id));
        await Assert.ThrowsAsync<RequestValidationException>(() => Service().LinkToRequirementAsync(fr.Id, story.Id)); // an FR is not downstream
        await Assert.ThrowsAsync<RequestValidationException>(() => Service().LinkToRequirementAsync(story.Id, Guid.NewGuid()));
        Assert.Null(await Service().LinkToRequirementAsync(Guid.NewGuid(), fr.Id));

        Assert.Empty(InMemoryAiReapDb.Rows(_db.ArtifactRelationships));
    }

    [Fact]
    public async Task An_artifact_in_another_project_cannot_be_linked_to()
    {
        var story = Add(ArtifactType.UserStory, "US-001");
        var elsewhere = Add(ArtifactType.FunctionalRequirement, "FR-001");
        elsewhere.ProjectId = Guid.NewGuid();

        await Assert.ThrowsAsync<RequestValidationException>(() => Service().LinkToRequirementAsync(story.Id, elsewhere.Id));
    }

    [Fact]
    public async Task A_link_to_a_rejected_requirement_does_not_count_as_traced()
    {
        var rejected = Add(ArtifactType.FunctionalRequirement, "FR-001", ArtifactStatus.Rejected);
        Link(Add(ArtifactType.UserStory, "US-001"), rejected, RelationshipType.DerivedFrom);

        Assert.Equal(["US-001"], (await GapsAsync()).Orphans.Select(o => o.Code));
    }

    [Fact]
    public async Task An_nfr_only_linked_to_its_objective_is_untraced_and_one_a_story_derives_from_is_not()
    {
        var objective = Add(ArtifactType.BusinessObjective, "BO-001");
        var untraced = Add(ArtifactType.NonFunctionalRequirement, "NFR-001");
        Link(untraced, objective, RelationshipType.DerivedFrom);
        var traced = Add(ArtifactType.NonFunctionalRequirement, "NFR-002");
        Link(Add(ArtifactType.UserStory, "US-001"), traced, RelationshipType.DerivedFrom);

        var gaps = await GapsAsync();

        Assert.Equal(2, gaps.NonFunctionalRequirementCount);
        Assert.Equal(["NFR-001"], gaps.UntracedNonFunctionalRequirements.Select(n => n.Code));
        Assert.Empty(gaps.Orphans);
    }
}
