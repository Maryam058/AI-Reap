using System.Text.Json;
using AiReap.Application.Ai.Pipeline;
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

// S1 - an approval or rejection may name the version it was made on (UpdateArtifactStatusRequest.Version),
// and every artifact save treats a lost version race (DbUpdateConcurrencyException, from
// Artifact.CurrentVersion being a concurrency token) as a 409, in one save per operation.
//
// The real services over InMemoryAiReapDb: no SQL Server, no network. The in-memory fake does not
// enforce the token; its FailNextSave hook throws the exception SQL Server would. So this covers the
// services' handling of a conflict, NOT that SQL Server raises one for a real two-context race
// (MigrationModelConsistencyTests checks the token is in the model; the race itself is unverified here).
[Trait("Gap", "S1")]
public class ArtifactConcurrencyTests
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

    private sealed class NoImpact : IImpactAnalysisService
    {
        public Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StageNoticesForChangeAsync(Artifact changed, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForProjectAsync(Guid projectId, bool includeAcknowledged, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImpactNoticeResponse?> AcknowledgeAsync(Guid noticeId, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static readonly User Ba = new("ba", Roles.BusinessAnalyst);   // the requirements' author
    private static readonly User Ba2 = new("ba2", Roles.BusinessAnalyst);
    private static readonly User Reviewer = new("rev", Roles.Reviewer);
    private static readonly User Dev = new("dev", Roles.Developer);

    private static string Fr(string evidenceStatus) =>
        JsonSerializer.Serialize(new FunctionalRequirementPayload
        {
            Actor = "Clerk",
            ExpectedResult = "Booking stored.",
            RequirementOrigin = "explicit",
            Evidence = [new EvidenceQuotePayload
            {
                From = "srs", Quote = "the clerk records every booking", SourceId = "FR-001", Status = evidenceStatus,
                Reason = evidenceStatus == "verified" ? null : "not_found", MatchCount = evidenceStatus == "verified" ? 1 : 0
            }],
            EvidenceStatus = evidenceStatus,
            EvidenceCheck = RequirementEvidence.CheckVersion
        });

    private sealed class Workspace
    {
        public InMemoryAiReapDb Db { get; } = new();
        public Guid ProjectId { get; } = Guid.NewGuid();

        public Artifact Add(ArtifactType type, string code, string dataJson = "{}", ArtifactStatus status = ArtifactStatus.AiGenerated, int version = 1)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = ProjectId, ArtifactType = type, Code = code, Title = code, Status = status,
                Origin = ArtifactOrigin.Ai, DataJson = dataJson, CurrentVersion = version,
                ApprovedVersion = ArtifactStatusTransitions.IsApprovedOrLater(status) ? version : null,
                CreatedByUserId = "ba", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        public ArtifactService Artifacts(User user) => new(Db, user, new OpenAccess(), new NoImpact());

        public ArtifactWorkService Work(User user) => new(Db, user, new OpenAccess());
    }

    private static object State(Artifact a) => (a.Status, a.DataJson, a.CurrentVersion, a.ApprovedVersion, a.Title, a.UpdatedAt, a.UpdatedByUserId);

    private static void AssertNoDecision(Workspace w, Artifact artifact, object before)
    {
        Assert.Equal(before, State(artifact));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactReviews));
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
        Assert.Equal(0, w.Db.SaveCount);
    }

    private static UpdateArtifactStatusRequest Decision(ArtifactStatus status, int? version) =>
        new(status, status == ArtifactStatus.Rejected ? "Not in the SRS." : null, version);

    // ---- the decision's version -----------------------------------------------------------------------

    [Theory]
    [InlineData(ArtifactStatus.Approved, 1)]
    [InlineData(ArtifactStatus.Approved, 3)]
    [InlineData(ArtifactStatus.Rejected, 1)]
    [InlineData(ArtifactStatus.Rejected, 3)]
    public async Task A_decision_on_another_version_is_refused_and_changes_nothing(ArtifactStatus status, int staleVersion)
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), version: 2);
        var execution = new AIExecution { Id = Guid.NewGuid(), ProducedArtifactId = fr.Id, Accepted = null };
        w.Db.AIExecutions.Add(execution);
        var before = State(fr);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Reviewer).UpdateStatusAsync(fr.Id, Decision(status, staleVersion)));

        Assert.Contains($"v{staleVersion} is not the current version of FR-001 (v2)", refused.Message);
        Assert.Contains(ArtifactConcurrency.ChangedMessage, refused.Message);
        AssertNoDecision(w, fr, before);
        Assert.Null(execution.Accepted);
    }

    [Theory]
    [InlineData(ArtifactStatus.Approved)]
    [InlineData(ArtifactStatus.Rejected)]
    public async Task A_decision_on_the_current_version_is_recorded_against_it(ArtifactStatus status)
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), version: 2);

        var decided = await w.Artifacts(Reviewer).UpdateStatusAsync(fr.Id, Decision(status, 2));

        Assert.Equal(status, decided!.Status);
        Assert.Equal(2, decided.CurrentVersion);
        Assert.Equal(status == ArtifactStatus.Approved ? 2 : null, fr.ApprovedVersion);
        Assert.Equal(2, Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactReviews)).VersionNumber);
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Theory]
    [InlineData(ArtifactStatus.Approved)]
    [InlineData(ArtifactStatus.Rejected)]
    public async Task A_decision_without_a_version_is_accepted_as_before(ArtifactStatus status)
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), version: 2);

        var decided = await w.Artifacts(Reviewer).UpdateStatusAsync(fr.Id, new UpdateArtifactStatusRequest(status, status == ArtifactStatus.Rejected ? "No." : null));

        Assert.Equal(status, decided!.Status);
        Assert.Equal(1, w.Db.SaveCount);
        // The JSON contract: a client that never sends "version" still binds.
        var legacy = JsonSerializer.Deserialize<UpdateArtifactStatusRequest>("{\"status\":3,\"comment\":null}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(legacy!.Version);
    }

    [Fact]
    public async Task The_version_only_guards_decisions_not_other_workflow_moves()
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), version: 2);

        var moved = await w.Artifacts(Ba2).UpdateStatusAsync(fr.Id, new UpdateArtifactStatusRequest(ArtifactStatus.UnderReview, null, 1));

        Assert.Equal(ArtifactStatus.UnderReview, moved!.Status);
    }

    [Fact]
    public async Task A_matching_version_does_not_bypass_the_author_or_evidence_rules()
    {
        var w = new Workspace();
        var verified = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"));
        var unverified = w.Add(ArtifactType.FunctionalRequirement, "FR-002", Fr("unverified"));

        // G3 P2 - the author cannot approve their own requirement, whatever version they name.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Artifacts(Ba).UpdateStatusAsync(verified.Id, Decision(ArtifactStatus.Approved, 1)));
        // G3 - the evidence gate still refuses.
        var blocked = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Reviewer).UpdateStatusAsync(unverified.Id, Decision(ArtifactStatus.Approved, 1)));
        Assert.Contains(RequirementApprovalEvidence.EvidenceUnverified, blocked.Message);
        // A rejection still needs a reason.
        await Assert.ThrowsAsync<RequestValidationException>(() => w.Artifacts(Reviewer).UpdateStatusAsync(verified.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Rejected, " ", 1)));

        Assert.Equal(ArtifactStatus.AiGenerated, verified.Status);
        Assert.Equal(ArtifactStatus.AiGenerated, unverified.Status);
        Assert.Equal(0, w.Db.SaveCount);
    }

    // ---- a lost race at save time -------------------------------------------------------------------

    private static DbUpdateConcurrencyException LostRace() => new("Database operation expected to affect 1 row(s) but actually affected 0 row(s).");

    [Theory]
    [InlineData(ArtifactStatus.Approved)]
    [InlineData(ArtifactStatus.Rejected)]
    public async Task A_decision_that_loses_the_race_is_a_conflict_not_a_success(ArtifactStatus status)
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"));
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Reviewer).UpdateStatusAsync(fr.Id, Decision(status, 1)));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount)); // one save, and it did not succeed
    }

    [Fact]
    public async Task An_edit_that_loses_the_race_is_a_conflict_in_its_only_save()
    {
        var w = new Workspace();
        var story = w.Add(ArtifactType.UserStory, "US-001", "{\"Story\":\"As a clerk\"}");
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Ba2).UpdateAsync(story.Id, new UpdateArtifactRequest("Renamed", null, null, "edit")));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount)); // the version row was in the same, failed save
    }

    [Fact]
    public async Task Two_edits_from_the_same_version_cannot_both_report_success()
    {
        // What SQL Server does to the second of two edits read at v1: its UPDATE ... WHERE CurrentVersion = 1
        // matches no row. Simulated here; the first edit is saved, the second is refused.
        var w = new Workspace();
        var story = w.Add(ArtifactType.UserStory, "US-001", "{\"Story\":\"As a clerk\"}");

        var first = await w.Artifacts(Ba).UpdateAsync(story.Id, new UpdateArtifactRequest("First", null, null, null));
        Assert.Equal(2, first!.CurrentVersion);

        w.Db.FailNextSave = LostRace();
        await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Ba2).UpdateAsync(story.Id, new UpdateArtifactRequest("Second", null, null, null)));
        Assert.Equal((1, 1), (w.Db.SaveCount, w.Db.FailedSaveCount));
    }

    [Fact]
    public async Task An_NFR_confirmation_that_loses_the_race_is_a_conflict()
    {
        var w = new Workspace();
        var nfr = w.Add(ArtifactType.NonFunctionalRequirement, "NFR-001",
            "{\"Category\":\"Performance\",\"Description\":\"Fast search.\",\"TargetValue\":\"2 seconds\",\"AssumptionStatus\":\"proposed_assumption\"}");
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Artifacts(Ba2).ConfirmNfrTargetAsync(nfr.Id, new ConfirmNfrTargetRequest(1)));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount));
    }

    [Fact]
    public async Task Approving_an_answered_clarification_saves_the_approval_and_the_resolution_together()
    {
        var w = new Workspace();
        var question = w.Add(ArtifactType.ClarificationQuestion, "CQ-001",
            JsonSerializer.Serialize(new ClarificationQuestionPayload { ClarificationStatus = "Answered", Answer = "Yes." }));

        var approved = await w.Artifacts(Reviewer).UpdateStatusAsync(question.Id, Decision(ArtifactStatus.Approved, 1));

        Assert.Equal(1, w.Db.SaveCount); // one save: no approved-but-unresolved state can be left behind
        Assert.Equal((ArtifactStatus.Approved, 2, 2), (approved!.Status, approved.CurrentVersion, question.ApprovedVersion));
        Assert.Contains("Resolved", question.DataJson);
        var version = Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
        Assert.Equal((2, ArtifactStatus.Approved), (version.VersionNumber, version.Status));
        Assert.Equal(1, Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactReviews)).VersionNumber); // the version reviewed

        var w2 = new Workspace();
        var question2 = w2.Add(ArtifactType.ClarificationQuestion, "CQ-002",
            JsonSerializer.Serialize(new ClarificationQuestionPayload { ClarificationStatus = "Answered", Answer = "Yes." }));
        w2.Db.FailNextSave = LostRace();
        await Assert.ThrowsAsync<ConflictException>(() => w2.Artifacts(Reviewer).UpdateStatusAsync(question2.Id, Decision(ArtifactStatus.Approved, 1)));
        Assert.Equal((1, 0), (w2.Db.FailedSaveCount, w2.Db.SaveCount));
    }

    [Fact]
    public async Task Work_and_the_promotion_it_causes_are_saved_together()
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), ArtifactStatus.Approved);
        var task = w.Add(ArtifactType.ImplementationTask, "TASK-001", status: ArtifactStatus.Approved);
        w.Db.ArtifactRelationships.Add(new ArtifactRelationship { Id = Guid.NewGuid(), SourceArtifactId = task.Id, TargetArtifactId = fr.Id, RelationshipType = RelationshipType.Implements, CreatedAt = DateTime.UtcNow });

        var recorded = await w.Work(Dev).RecordAsync(task.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, "Done."));

        Assert.Equal(WorkOutcome.TaskCompleted, recorded.Outcome);
        Assert.Equal(ArtifactStatus.Implemented, fr.Status);
        Assert.Equal(1, w.Db.SaveCount);
        Assert.Single(InMemoryAiReapDb.Rows(w.Db.ArtifactWorkRecords)); // counted once, not twice
    }

    [Fact]
    public async Task Work_whose_promotion_loses_the_race_is_a_conflict_not_a_partial_save()
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), ArtifactStatus.Approved);
        var task = w.Add(ArtifactType.ImplementationTask, "TASK-001", status: ArtifactStatus.Approved);
        w.Db.ArtifactRelationships.Add(new ArtifactRelationship { Id = Guid.NewGuid(), SourceArtifactId = task.Id, TargetArtifactId = fr.Id, RelationshipType = RelationshipType.Implements, CreatedAt = DateTime.UtcNow });
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Work(Dev).RecordAsync(task.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null)));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount)); // the record was never saved on its own
    }

    [Fact]
    public async Task Only_a_version_conflict_becomes_a_409()
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"));

        w.Db.FailNextSave = new DbUpdateException("unique index");
        await Assert.ThrowsAsync<DbUpdateException>(() => w.Artifacts(Reviewer).UpdateStatusAsync(fr.Id, Decision(ArtifactStatus.Approved, 1)));

        w.Db.FailNextSave = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => w.Artifacts(Reviewer).UpdateStatusAsync(fr.Id, Decision(ArtifactStatus.Rejected, 1)));

        Assert.Equal(0, w.Db.SaveCount);
    }

    // ---- work records: concurrency checks on what the promotion decision read --------------------------
    // SIMULATED: InMemoryAiReapDb only records which artifacts MarkArtifactForConcurrencyCheck was asked
    // to check, and FailNextSave throws what SQL Server would. These tests show the service requests the
    // checks and handles a conflict; that EF Core turns a check into an UPDATE under the RowVersion token
    // is inspected offline in MigrationModelConsistencyTests; a real SQL Server race is not exercised.

    private static void Implements(Workspace w, Artifact task, Artifact fr) =>
        w.Db.ArtifactRelationships.Add(new ArtifactRelationship { Id = Guid.NewGuid(), SourceArtifactId = task.Id, TargetArtifactId = fr.Id, RelationshipType = RelationshipType.Implements, CreatedAt = DateTime.UtcNow });

    private static void TestedBy(Workspace w, Artifact fr, Artifact test) =>
        w.Db.ArtifactRelationships.Add(new ArtifactRelationship { Id = Guid.NewGuid(), SourceArtifactId = fr.Id, TargetArtifactId = test.Id, RelationshipType = RelationshipType.TestedBy, CreatedAt = DateTime.UtcNow });

    private static readonly User Qa = new("qa", Roles.QA);

    [Fact]
    public async Task Work_requests_a_check_on_its_task_and_on_every_requirement_the_promotion_read()
    {
        var w = new Workspace();
        var promoted = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), ArtifactStatus.Approved);
        var waiting = w.Add(ArtifactType.FunctionalRequirement, "FR-002", Fr("verified"), ArtifactStatus.Approved);  // another task still open
        var notApproved = w.Add(ArtifactType.FunctionalRequirement, "FR-003", Fr("verified"), ArtifactStatus.UnderReview);
        var task = w.Add(ArtifactType.ImplementationTask, "TASK-001", status: ArtifactStatus.Approved);
        var other = w.Add(ArtifactType.ImplementationTask, "TASK-002", status: ArtifactStatus.Approved);
        Implements(w, task, promoted);
        Implements(w, task, waiting);
        Implements(w, task, notApproved);
        Implements(w, other, waiting);

        await w.Work(Dev).RecordAsync(task.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null));

        Assert.Equal((ArtifactStatus.Implemented, ArtifactStatus.Approved, ArtifactStatus.UnderReview), (promoted.Status, waiting.Status, notApproved.Status));
        // Promoted or not, each requirement whose state decided the outcome is checked; the trigger too.
        Assert.Equal(new[] { task.Id, promoted.Id, waiting.Id, notApproved.Id }.Order(), w.Db.ConcurrencyChecks.Order());
        Assert.DoesNotContain(other.Id, w.Db.ConcurrencyChecks); // only read for its records, not changed by them
        Assert.Equal(1, w.Db.SaveCount);
    }

    [Fact]
    public async Task A_test_result_requests_a_check_on_its_test_case_and_the_requirement_it_tests()
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), ArtifactStatus.Implemented);
        var test = w.Add(ArtifactType.TestCase, "TC-001", status: ArtifactStatus.Approved);
        TestedBy(w, fr, test);

        await w.Work(Qa).RecordAsync(test.Id, new RecordWorkRequest(WorkOutcome.TestPassed, null));

        Assert.Equal(ArtifactStatus.Verified, fr.Status);
        Assert.Equal(new[] { test.Id, fr.Id }.Order(), w.Db.ConcurrencyChecks.Order());
    }

    [Fact]
    public async Task Work_with_no_linked_requirement_checks_only_its_task_and_refused_work_checks_nothing()
    {
        var w = new Workspace();
        var task = w.Add(ArtifactType.ImplementationTask, "TASK-001", status: ArtifactStatus.Approved);
        await w.Work(Dev).RecordAsync(task.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null));
        Assert.Equal(new[] { task.Id }, w.Db.ConcurrencyChecks);

        var w2 = new Workspace();
        var draft = w2.Add(ArtifactType.ImplementationTask, "TASK-002");
        await Assert.ThrowsAsync<ConflictException>(() => w2.Work(Dev).RecordAsync(draft.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w2.Work(Qa).RecordAsync(draft.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null)));
        Assert.Empty(w2.Db.ConcurrencyChecks);
        Assert.Equal(0, w2.Db.SaveCount);
    }

    [Fact]
    public async Task Work_that_loses_the_race_without_promoting_anything_is_still_a_conflict()
    {
        // The case CurrentVersion alone could not catch: nothing on the requirement changes, so only the
        // touch (and its RowVersion) puts it in the save. Simulated conflict.
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), ArtifactStatus.Approved);
        var task = w.Add(ArtifactType.ImplementationTask, "TASK-001", status: ArtifactStatus.Approved);
        var open = w.Add(ArtifactType.ImplementationTask, "TASK-002", status: ArtifactStatus.Approved);
        Implements(w, task, fr);
        Implements(w, open, fr);
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => w.Work(Dev).RecordAsync(task.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null)));

        Assert.Equal(ArtifactConcurrency.ChangedMessage, refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount)); // no successful save is reported
        Assert.Equal(ArtifactStatus.Approved, fr.Status);
        Assert.Contains(fr.Id, w.Db.ConcurrencyChecks);
    }

    [Fact]
    public async Task Completing_the_last_two_tasks_at_once_conflicts_once_and_the_retry_promotes()
    {
        // SIMULATED interleaving of two developers completing FR-001's last two tasks at the same time.
        // B reads before A commits, so B sees TASK-001 still open and would not promote; A also sees
        // TASK-002 open. Without the requirement touch both would save and FR-001 would stay Approved
        // with every task complete. With it, B's save fails on FR-001's RowVersion (thrown here by
        // FailNextSave) and B's retry, reading A's completion, promotes.
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"), ArtifactStatus.Approved);
        var t1 = w.Add(ArtifactType.ImplementationTask, "TASK-001", status: ArtifactStatus.Approved);
        var t2 = w.Add(ArtifactType.ImplementationTask, "TASK-002", status: ArtifactStatus.Approved);
        Implements(w, t1, fr);
        Implements(w, t2, fr);
        var devB = new User("dev-b", Roles.Developer);

        // B: evaluates before A's record exists; its save loses to A's commit.
        w.Db.FailNextSave = LostRace();
        await Assert.ThrowsAsync<ConflictException>(() => w.Work(devB).RecordAsync(t2.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null)));
        Assert.Equal(ArtifactStatus.Approved, fr.Status);
        Assert.Contains(fr.Id, w.Db.ConcurrencyChecks);
        // The fake models no transaction: drop B's unsaved record as SQL Server's rollback would.
        Assert.Equal(1, InMemoryAiReapDb.Rows(w.Db.ArtifactWorkRecords).RemoveAll(r => r.ArtifactId == t2.Id));

        // A: commits; TASK-002 is still open as far as the database knows, so no promotion yet.
        await w.Work(Dev).RecordAsync(t1.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null));
        Assert.Equal(ArtifactStatus.Approved, fr.Status);

        // B retries (by hand - nothing retries automatically): both completions are seen, FR-001 promoted.
        var retried = await w.Work(devB).RecordAsync(t2.Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null));
        Assert.Equal(WorkOutcome.TaskCompleted, retried.Outcome);
        Assert.Equal(ArtifactStatus.Implemented, fr.Status);
        Assert.Equal((1, 2), (w.Db.FailedSaveCount, w.Db.SaveCount));
        Assert.Equal(2, InMemoryAiReapDb.Rows(w.Db.ArtifactWorkRecords).Count); // one per task
    }

    // ---- business objective links ------------------------------------------------------------------------

    // Stands in for SqlException: ArtifactPersistence reads the error Number by reflection, and SQL
    // Server names the violated index in the message.
    private sealed class FakeSqlError(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }

    private static DbUpdateException UniqueViolation(string index) =>
        new("save failed", new FakeSqlError(2601, $"Cannot insert duplicate key row in object 'dbo.ArtifactRelationships' with unique index '{index}'. The duplicate key value is (...)."));

    private static (Workspace W, Artifact Fr, Artifact Objective, TraceabilityService Service) ObjectiveLinks()
    {
        var w = new Workspace();
        var fr = w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"));
        var objective = w.Add(ArtifactType.BusinessObjective, "BO-001");
        return (w, fr, objective, new TraceabilityService(w.Db, new OpenAccess(), Ba));
    }

    [Fact]
    public async Task Objective_links_that_lose_a_version_race_are_a_conflict()
    {
        var (w, fr, objective, service) = ObjectiveLinks();
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => service.SetBusinessObjectivesAsync(fr.Id, [objective.Id]));

        Assert.Equal(TraceabilityService.ObjectivesChangedMessage("FR-001"), refused.Message);
        Assert.Contains("not saved", refused.Message);
        Assert.Contains("reload", refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount));
    }

    [Fact]
    public async Task Objective_links_that_collide_on_the_link_index_are_a_conflict()
    {
        var (w, fr, objective, service) = ObjectiveLinks();
        w.Db.FailNextSave = UniqueViolation(TraceabilityService.ObjectiveLinkIndex);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => service.SetBusinessObjectivesAsync(fr.Id, [objective.Id]));

        Assert.Equal(TraceabilityService.ObjectivesChangedMessage("FR-001"), refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount));
    }

    [Fact]
    public async Task Other_objective_link_save_failures_propagate_unchanged()
    {
        var (w, fr, objective, service) = ObjectiveLinks();

        var otherIndex = UniqueViolation("IX_Artifacts_ProjectId_ArtifactType_Code");
        w.Db.FailNextSave = otherIndex;
        Assert.Same(otherIndex, await Assert.ThrowsAsync<DbUpdateException>(() => service.SetBusinessObjectivesAsync(fr.Id, [objective.Id])));

        var foreignKey = new DbUpdateException("save failed", new FakeSqlError(547, "The INSERT statement conflicted with the FOREIGN KEY constraint."));
        w.Db.FailNextSave = foreignKey;
        Assert.Same(foreignKey, await Assert.ThrowsAsync<DbUpdateException>(() => service.SetBusinessObjectivesAsync(fr.Id, [objective.Id])));

        var plain = new DbUpdateException("save failed");
        w.Db.FailNextSave = plain;
        Assert.Same(plain, await Assert.ThrowsAsync<DbUpdateException>(() => service.SetBusinessObjectivesAsync(fr.Id, [objective.Id])));

        Assert.Equal(0, w.Db.SaveCount);
    }

    // ---- project deletion ----------------------------------------------------------------------------------

    private static (Workspace W, ProjectService Service) DeletableProject()
    {
        var w = new Workspace();
        w.Db.Projects.Add(new Project { Id = w.ProjectId, Name = "Delete me", CreatedByUserId = "ba" });
        w.Add(ArtifactType.FunctionalRequirement, "FR-001", Fr("verified"));
        return (w, new ProjectService(w.Db, new User("admin", Roles.Administrator)));
    }

    [Fact]
    public async Task A_project_deletion_that_loses_a_race_is_a_conflict()
    {
        var (w, service) = DeletableProject();
        w.Db.FailNextSave = LostRace();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(w.ProjectId));

        Assert.Equal(ProjectService.DeleteConflictMessage, refused.Message);
        Assert.Contains("not deleted", refused.Message);
        Assert.Equal((1, 0), (w.Db.FailedSaveCount, w.Db.SaveCount)); // one save, and it did not succeed
    }

    [Fact]
    public async Task Other_project_deletion_failures_propagate_unchanged()
    {
        foreach (var failure in new[]
        {
            new DbUpdateException("save failed", new FakeSqlError(547, "The DELETE statement conflicted with the REFERENCE constraint.")),
            UniqueViolation(TraceabilityService.ObjectiveLinkIndex),
            new DbUpdateException("save failed")
        })
        {
            var (w, service) = DeletableProject();
            w.Db.FailNextSave = failure;
            Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteAsync(w.ProjectId)));
            Assert.Equal(0, w.Db.SaveCount);
        }
    }
}
