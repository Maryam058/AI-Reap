using System.Text.Json;
using AiReap.Application.Agents;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Projects;
using AiReap.Application.Traceability;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiReap.Tests;

// Reliability fixes after the RowVersion audit, over InMemoryAiReapDb: no SQL Server, no AI provider,
// no network.
//   R1 - an edit of approved content, its new version and its impact notices are written by one save.
//   Story dependencies - a concurrent removal is answered as "already gone" (404); a concurrent duplicate
//        insert that hits the unique index is a 409. Other database failures propagate.
//   Agent steps - the save that ends a step is guarded like the claim that started it.
//
// What this does NOT show: the fake models no transaction (rows added before a failed save stay in its
// lists, and ArtifactPersistence.Undo has no change tracker to restore), and it raises the exceptions SQL
// Server would rather than SQL Server raising them. So "nothing committed" below means "no save
// succeeded", and that the notices were in the change set of the one save; that SQL Server writes that
// save atomically, and that real concurrent requests produce these exceptions, needs a database.
public class ReliabilityFixTests
{
    private sealed class User(string id, params string[] roles) : ICurrentUser
    {
        public string UserId => id;
        public bool IsInRole(string role) => roles.Contains(role);
    }

    private sealed class OpenAccess : IProjectAccessService
    {
        public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Stands in for SqlException: ArtifactPersistence reads the error Number by reflection, and SQL
    // Server names the violated index in the message.
    private sealed class FakeSqlError(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }

    private static readonly User Ba = new("ba", Roles.BusinessAnalyst);

    private static DbUpdateConcurrencyException LostRace() => new("Database operation expected to affect 1 row(s) but actually affected 0 row(s).");

    private static DbUpdateException SqlFailure(int number, string message) => new("save failed", new FakeSqlError(number, message));

    private static DbUpdateException UniqueViolation(string index) =>
        SqlFailure(2601, $"Cannot insert duplicate key row in object 'dbo.ArtifactRelationships' with unique index '{index}'. The duplicate key value is (...).");

    private sealed class Workspace
    {
        public InMemoryAiReapDb Db { get; } = new();
        public Guid ProjectId { get; } = Guid.NewGuid();

        public Artifact Add(ArtifactType type, string code, string dataJson = "{}", ArtifactStatus status = ArtifactStatus.AiGenerated)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = ProjectId, ArtifactType = type, Code = code, Title = code, Status = status,
                Origin = ArtifactOrigin.Ai, DataJson = dataJson, CurrentVersion = 1,
                ApprovedVersion = ArtifactStatusTransitions.IsApprovedOrLater(status) ? 1 : null,
                CreatedByUserId = "ba", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        public ArtifactRelationship Link(Artifact source, RelationshipType type, Artifact target)
        {
            var link = new ArtifactRelationship
            {
                Id = Guid.NewGuid(), SourceArtifactId = source.Id, TargetArtifactId = target.Id, RelationshipType = type, CreatedAt = DateTime.UtcNow
            };
            Db.ArtifactRelationships.Add(link);
            return link;
        }

        // The real impact analysis, so notice selection is what production does.
        public ArtifactService Artifacts(User user) => new(Db, user, new OpenAccess(), new ImpactAnalysisService(Db, new OpenAccess(), user));

        public List<ArtifactVersion> Versions => InMemoryAiReapDb.Rows(Db.ArtifactVersions);
        public List<ArtifactImpactNotice> Notices => InMemoryAiReapDb.Rows(Db.ArtifactImpactNotices);
        public List<ArtifactRelationship> Links => InMemoryAiReapDb.Rows(Db.ArtifactRelationships);
    }

    // An approved story with a criterion derived from it: editing the story must notify the criterion.
    private static (Workspace W, Artifact Story, Artifact Criterion) ApprovedStoryWithCriterion()
    {
        var w = new Workspace();
        var story = w.Add(ArtifactType.UserStory, "US-001", "{\"Story\":\"As a clerk\"}", ArtifactStatus.Approved);
        var criterion = w.Add(ArtifactType.AcceptanceCriterion, "AC-001", "{}", ArtifactStatus.Approved);
        w.Link(criterion, RelationshipType.DerivedFrom, story);
        return (w, story, criterion);
    }

    private static (Workspace W, Artifact Question, Artifact Requirement) ApprovedClarificationWithDependent()
    {
        var w = new Workspace();
        var question = w.Add(ArtifactType.ClarificationQuestion, "CQ-001",
            JsonSerializer.Serialize(new ClarificationQuestionPayload { Question = "Which bookings?" }), ArtifactStatus.Approved);
        var requirement = w.Add(ArtifactType.FunctionalRequirement, "FR-001", "{}", ArtifactStatus.Approved);
        w.Link(requirement, RelationshipType.DerivedFrom, question);
        return (w, question, requirement);
    }

    // ---- R1: content edit ----------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_edit_writes_its_version_and_notices_in_one_save()
    {
        var (w, story, criterion) = ApprovedStoryWithCriterion();

        var updated = await w.Artifacts(Ba).UpdateAsync(story.Id, new UpdateArtifactRequest("Renamed", null, null, "edit"));

        Assert.Equal((2, ArtifactStatus.UnderReview), (updated!.CurrentVersion, updated.Status));
        Assert.Equal(1, w.Db.SaveCount);
        var version = Assert.Single(w.Versions);
        Assert.Equal((2, ArtifactStatus.UnderReview), (version.VersionNumber, version.Status));
        Assert.Contains("re-review required", version.Reason);
        var notice = Assert.Single(w.Notices);
        Assert.Equal((story.Id, 2, criterion.Id), (notice.SourceArtifactId, notice.SourceVersion, notice.AffectedArtifactId));
        Assert.Null(notice.AcknowledgedAt);
        Assert.Equal("AC-001 derived from US-001", notice.Path);
    }

    [Fact]
    public async Task An_approved_edit_that_loses_the_race_commits_nothing_and_its_notices_were_in_that_save()
    {
        var (w, story, _) = ApprovedStoryWithCriterion();
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Ba).UpdateAsync(story.Id, new UpdateArtifactRequest("Renamed", null, null, "edit")));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        // No save succeeded, and none was attempted after the failed one: the version and the notice
        // (still in the fake's lists, which model no rollback) were staged for that single save.
        Assert.Equal((0, 1), (w.Db.SaveCount, w.Db.FailedSaveCount));
        Assert.Single(w.Versions);
        Assert.Single(w.Notices);
    }

    [Fact]
    public async Task Any_other_failure_of_the_combined_save_propagates_unchanged()
    {
        var (w, story, _) = ApprovedStoryWithCriterion();
        var failure = SqlFailure(547, "The UPDATE statement conflicted with the FOREIGN KEY constraint.");
        w.Db.FailNextSave = failure;

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => w.Artifacts(Ba).UpdateAsync(story.Id, new UpdateArtifactRequest("Renamed", null, null, "edit")));

        Assert.Same(failure, thrown);
        Assert.Equal((0, 1), (w.Db.SaveCount, w.Db.FailedSaveCount));
    }

    [Fact]
    public async Task A_second_approved_edit_supersedes_the_open_notice_instead_of_duplicating_it()
    {
        var (w, story, criterion) = ApprovedStoryWithCriterion();
        var older = new ArtifactImpactNotice
        {
            Id = Guid.NewGuid(), ProjectId = w.ProjectId, SourceArtifactId = story.Id, SourceVersion = 1,
            AffectedArtifactId = criterion.Id, Path = "AC-001 derived from US-001", CreatedByUserId = "ba", CreatedAt = DateTime.UtcNow
        };
        w.Db.ArtifactImpactNotices.Add(older);

        await w.Artifacts(Ba).UpdateAsync(story.Id, new UpdateArtifactRequest("Renamed", null, null, null));

        Assert.Equal(1, w.Db.SaveCount);
        Assert.Equal(2, w.Notices.Count);
        Assert.NotNull(older.AcknowledgedAt);
        Assert.Equal("Superseded by v2 of US-001.", older.AcknowledgementNote);
        Assert.Single(w.Notices, n => n.AcknowledgedAt is null && n.SourceVersion == 2);
    }

    [Fact]
    public async Task An_edit_of_unapproved_content_raises_no_notices_in_its_one_save()
    {
        var w = new Workspace();
        var story = w.Add(ArtifactType.UserStory, "US-001", "{\"Story\":\"As a clerk\"}");
        var criterion = w.Add(ArtifactType.AcceptanceCriterion, "AC-001");
        w.Link(criterion, RelationshipType.DerivedFrom, story);

        await w.Artifacts(Ba).UpdateAsync(story.Id, new UpdateArtifactRequest("Renamed", null, null, null));

        Assert.Equal(1, w.Db.SaveCount);
        Assert.Single(w.Versions);
        Assert.Empty(w.Notices);
    }

    // ---- R1: clarification answer --------------------------------------------------------------------

    [Fact]
    public async Task An_approved_clarification_answer_writes_its_version_and_notices_in_one_save()
    {
        var (w, question, requirement) = ApprovedClarificationWithDependent();

        var answered = await w.Artifacts(Ba).AnswerClarificationAsync(question.Id, new ClarificationAnswerRequest("Only confirmed ones.", false));

        Assert.Equal((2, ArtifactStatus.UnderReview), (answered!.CurrentVersion, answered.Status));
        Assert.Equal(1, w.Db.SaveCount);
        var version = Assert.Single(w.Versions);
        Assert.StartsWith("Clarification answered", version.Reason);
        var notice = Assert.Single(w.Notices);
        Assert.Equal((question.Id, 2, requirement.Id), (notice.SourceArtifactId, notice.SourceVersion, notice.AffectedArtifactId));
    }

    [Fact]
    public async Task An_approved_clarification_answer_that_loses_the_race_commits_nothing()
    {
        var (w, question, _) = ApprovedClarificationWithDependent();
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            w.Artifacts(Ba).AnswerClarificationAsync(question.Id, new ClarificationAnswerRequest("Only confirmed ones.", false)));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        Assert.Equal((0, 1), (w.Db.SaveCount, w.Db.FailedSaveCount));
        Assert.Single(w.Versions);
        Assert.Single(w.Notices);
    }

    // ---- story dependencies --------------------------------------------------------------------------

    private static (Workspace W, Artifact StoryB, Artifact StoryA) TwoStories()
    {
        var w = new Workspace();
        var a = w.Add(ArtifactType.UserStory, "US-001");
        var b = w.Add(ArtifactType.UserStory, "US-002");
        return (w, b, a);
    }

    [Fact]
    public async Task Removing_a_dependency_another_request_removed_first_is_answered_as_already_gone()
    {
        var (w, b, a) = TwoStories();
        w.Link(b, RelationshipType.DependsOn, a);
        // SQL Server's answer to a DELETE of a row that is no longer there.
        w.Db.FailNextSave = LostRace();

        Assert.False(await w.Artifacts(Ba).RemoveDependencyAsync(b.Id, a.Id));
        Assert.Equal((0, 1), (w.Db.SaveCount, w.Db.FailedSaveCount));
    }

    [Fact]
    public async Task Removing_a_dependency_still_succeeds_normally_and_any_other_failure_propagates()
    {
        var (w, b, a) = TwoStories();
        w.Link(b, RelationshipType.DependsOn, a);
        Assert.True(await w.Artifacts(Ba).RemoveDependencyAsync(b.Id, a.Id));
        Assert.Equal(1, w.Db.SaveCount);

        var (w2, b2, a2) = TwoStories();
        w2.Link(b2, RelationshipType.DependsOn, a2);
        var failure = SqlFailure(547, "The DELETE statement conflicted with a constraint.");
        w2.Db.FailNextSave = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => w2.Artifacts(Ba).RemoveDependencyAsync(b2.Id, a2.Id)));
    }

    [Fact]
    public async Task A_duplicate_dependency_inserted_concurrently_is_a_conflict()
    {
        var (w, b, a) = TwoStories();
        // Both requests passed the "already depends" check; this one's insert hits the unique index.
        w.Db.FailNextSave = UniqueViolation(TraceabilityService.ObjectiveLinkIndex);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Ba).AddDependencyAsync(b.Id, new AddDependencyRequest(a.Id)));

        Assert.Equal("US-002 already depends on US-001.", refused.Message);
        Assert.Equal((0, 1), (w.Db.SaveCount, w.Db.FailedSaveCount));
    }

    [Theory]
    [InlineData(2601, "IX_SomeOtherIndex")] // a unique violation on another index
    [InlineData(547, "IX_ArtifactRelationships_SourceArtifactId_RelationshipType_TargetArtifactId")] // not a unique violation
    public async Task Any_other_failure_adding_a_dependency_propagates(int number, string index)
    {
        var (w, b, a) = TwoStories();
        var failure = SqlFailure(number, $"Statement failed on '{index}'.");
        w.Db.FailNextSave = failure;

        Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => w.Artifacts(Ba).AddDependencyAsync(b.Id, new AddDependencyRequest(a.Id))));
    }

    [Fact]
    public async Task Adding_a_dependency_still_succeeds_normally()
    {
        var (w, b, a) = TwoStories();

        var added = await w.Artifacts(Ba).AddDependencyAsync(b.Id, new AddDependencyRequest(a.Id));

        Assert.Equal(a.Id, added!.RelatedArtifactId);
        Assert.Equal(1, w.Db.SaveCount);
        Assert.Single(w.Links, l => l.SourceArtifactId == b.Id && l.TargetArtifactId == a.Id && l.RelationshipType == RelationshipType.DependsOn);
    }

    // ---- agent step: the save that ends it -----------------------------------------------------------

    // A controllable agent: no AI provider. `whileRunning` stands in for what happens elsewhere while the
    // step runs (e.g. arming the fake to refuse the next save, as SQL Server would after a concurrent
    // change to the stage row).
    private sealed class FakeAgent(Action whileRunning) : IAgent
    {
        public int Runs { get; private set; }
        public AgentKind Kind => AgentKind.Requirements;
        public string DisplayName => "Requirements";
        public string Responsibility => "-";
        public string Inputs => "-";
        public string Outputs => "-";
        public IReadOnlyList<string> ApproverRoles => [Roles.BusinessAnalyst];
        public IReadOnlyList<ProjectStatus> LifecycleStages => [];
        public string LifecycleContribution => "-";

        public Task<AgentOutput> RunAsync(AgentContext context, CancellationToken cancellationToken)
        {
            Runs++;
            whileRunning();
            return Task.FromResult(new AgentOutput("done", new Dictionary<string, int>(), [], [], "-"));
        }
    }

    private static (InMemoryAiReapDb Db, Guid SourceId) AgentWorkspace()
    {
        var db = new InMemoryAiReapDb();
        var project = new Project { Id = Guid.NewGuid(), Status = ProjectStatus.Analysis };
        db.Projects.Add(project);
        var source = new RequirementSource { Id = Guid.NewGuid(), ProjectId = project.Id, RawText = "text", CreatedByUserId = "ba", CreatedAt = DateTime.UtcNow };
        db.RequirementSources.Add(source);
        return (db, source.Id);
    }

    [Fact]
    public async Task A_step_whose_final_save_loses_the_race_is_the_established_conflict_and_is_not_rerun()
    {
        var (db, sourceId) = AgentWorkspace();
        var agent = new FakeAgent(() => db.FailNextSave = LostRace());
        var orchestrator = new AgentOrchestrator(db, Ba, new OpenAccess(), [agent]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartAsync(sourceId));

        // The same message a lost claim gets (AgentsController answers it with 409).
        Assert.Equal("This step was just acted on by someone else. Reload the run to see its current state.", refused.Message);
        Assert.Equal(1, agent.Runs);
        Assert.Equal((2, 1), (db.SaveCount, db.FailedSaveCount)); // run created, step claimed; the final save refused
    }

    [Fact]
    public async Task Any_other_failure_of_a_steps_final_save_propagates_unchanged()
    {
        var (db, sourceId) = AgentWorkspace();
        var failure = SqlFailure(547, "The UPDATE statement conflicted with a constraint.");
        var agent = new FakeAgent(() => db.FailNextSave = failure);
        var orchestrator = new AgentOrchestrator(db, Ba, new OpenAccess(), [agent]);

        Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => orchestrator.StartAsync(sourceId)));
        Assert.Equal(1, agent.Runs);
    }

    [Fact]
    public async Task A_step_that_saves_normally_parks_at_the_human_checkpoint()
    {
        var (db, sourceId) = AgentWorkspace();
        var agent = new FakeAgent(() => { });
        var orchestrator = new AgentOrchestrator(db, Ba, new OpenAccess(), [agent]);

        var run = await orchestrator.StartAsync(sourceId);

        Assert.Equal(AgentRunStatus.AwaitingApproval, run.Status);
        Assert.Equal(AgentStageStatus.AwaitingApproval, Assert.Single(run.Stages).Status);
        Assert.Equal(3, db.SaveCount);
    }
}
