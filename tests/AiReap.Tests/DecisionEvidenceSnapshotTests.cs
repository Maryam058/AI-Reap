using System.Text.Json;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Application.Sdlc;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiReap.Tests;

// D3 - a gated request keeps its submission-time evidence (EvidenceSnapshotJson) and records the
// decision-time evidence beside it (DecisionEvidenceSnapshotJson), for approvals and rejections.
//
// The real ProjectLifecycleService over InMemoryAiReapDb, as in G3FollowUpTests: no AI provider, no SQL
// Server, no network. The column itself (and its migration) is not exercised against a database here.
[Trait("Gap", "D3")]
public class DecisionEvidenceSnapshotTests
{
    private const string SourceEvidenceLabel = "Requirement source evidence and NFR targets accepted";

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

    private sealed class User(string id, params string[] roles) : ICurrentUser
    {
        public string UserId => id;
        public bool IsInRole(string role) => roles.Contains(role);
    }

    private sealed class RoleDirectory(Dictionary<string, string[]> roles) : IUserRoleDirectory
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetActiveUserRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(userIds.Distinct().Where(roles.ContainsKey)
                .ToDictionary(u => u, u => (IReadOnlyList<string>)roles[u]));

        public Task<IReadOnlyList<string>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(roles.Where(r => r.Value.Contains(role)).Select(r => r.Key).ToList());
    }

    private sealed class OpenAccess : IProjectAccessService
    {
        public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // The real evidence service, made to fail on demand (as a database error or a cancellation would).
    private sealed class SwitchableEvidence(ILifecycleEvidenceService inner) : ILifecycleEvidenceService
    {
        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<StageEvidence>> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Failure is null ? inner.GetAsync(projectId, cancellationToken) : throw Failure;

        public Task<IReadOnlyList<StageCheckEvidence>> GetDetailedAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Failure is null ? inner.GetDetailedAsync(projectId, cancellationToken) : throw Failure;

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<StageEvidence>>> GetManyAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken = default) =>
            Failure is null ? inner.GetManyAsync(projectIds, cancellationToken) : throw Failure;
    }

    // Captures what the service logs, without a mocking library (as AiChatClientSelectionTests does).
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    // A project in Design whose Design and Testing exit checks all pass while FR-001 is verified.
    private sealed class Workspace
    {
        public static readonly User Dev = new("dev", Roles.Developer);
        public static readonly User Reviewer = new("rev", Roles.Reviewer);
        public static readonly User Admin = new("admin", Roles.Administrator);
        public static readonly User Admin2 = new("admin2", Roles.Administrator);

        private readonly RoleDirectory _roles = new(new()
        {
            ["dev"] = [Roles.Developer], ["rev"] = [Roles.Reviewer], ["qa"] = [Roles.QA], ["admin"] = [Roles.Administrator],
            ["admin2"] = [Roles.Administrator], ["ba"] = [Roles.BusinessAnalyst]
        });

        public InMemoryAiReapDb Db { get; } = new();
        public Project Project { get; }
        public Guid SourceId { get; } = Guid.NewGuid();
        public Artifact Fr { get; }
        public Exception? EvidenceFailure { get; set; }

        public Workspace(ProjectStatus status, string frStatus = "verified")
        {
            Project = new Project { Id = Guid.NewGuid(), Name = "D3 offline", Status = status, Objectives = "o", BusinessProblem = "p", Scope = "s", CreatedByUserId = "ba" };
            Db.Projects.Add(Project);
            foreach (var user in new[] { "dev", "rev", "qa", "admin", "admin2", "ba" })
            {
                Db.ProjectMembers.Add(new ProjectMember { Id = Guid.NewGuid(), ProjectId = Project.Id, UserId = user, AddedByUserId = "ba", AddedAt = DateTime.UtcNow });
            }

            Fr = Add(ArtifactType.FunctionalRequirement, "FR-001", Fr(frStatus));
            var story = Add(ArtifactType.UserStory, "US-001");
            var criterion = Add(ArtifactType.AcceptanceCriterion, "AC-001");
            var design = Add(ArtifactType.DesignArtifact, "DES-001");
            var api = Add(ArtifactType.ApiSpecification, "API-001");
            var entity = Add(ArtifactType.DataEntity, "DE-001");
            var task = Add(ArtifactType.ImplementationTask, "TASK-001");
            var test = Add(ArtifactType.TestCase, "TC-001");

            Link(story, Fr, RelationshipType.DerivedFrom);
            Link(criterion, story, RelationshipType.DerivedFrom);
            Link(task, Fr, RelationshipType.Implements);
            Link(Fr, test, RelationshipType.TestedBy);
            foreach (var designed in new[] { design, api, entity })
            {
                Db.ArtifactReviews.Add(new ArtifactReview
                {
                    Id = Guid.NewGuid(), ArtifactId = designed.Id, ReviewerUserId = "dev", ReviewerRole = Roles.Developer,
                    Decision = ReviewDecision.CommentOnly, VersionNumber = designed.CurrentVersion, ReviewedAt = DateTime.UtcNow
                });
            }

            Work(task, WorkOutcome.TaskCompleted, Roles.Developer);
            Work(test, WorkOutcome.TestPassed, Roles.QA);
        }

        private Artifact Add(ArtifactType type, string code, string dataJson = "{}")
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = Project.Id, ArtifactType = type, Code = code, Title = code, Status = ArtifactStatus.Approved,
                Origin = ArtifactOrigin.Ai, RequirementSourceId = SourceId, DataJson = dataJson, CurrentVersion = 1, ApprovedVersion = 1,
                CreatedByUserId = "ba", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            Db.Artifacts.Add(artifact);
            return artifact;
        }

        private void Link(Artifact source, Artifact target, RelationshipType type) =>
            Db.ArtifactRelationships.Add(new ArtifactRelationship { Id = Guid.NewGuid(), SourceArtifactId = source.Id, TargetArtifactId = target.Id, RelationshipType = type, CreatedAt = DateTime.UtcNow });

        private void Work(Artifact artifact, WorkOutcome outcome, string role) =>
            Db.ArtifactWorkRecords.Add(new ArtifactWorkRecord
            {
                Id = Guid.NewGuid(), ArtifactId = artifact.Id, VersionNumber = 1, Outcome = outcome, RecordedByUserId = role, RecordedByRole = role, RecordedAt = DateTime.UtcNow
            });

        public ProjectStageTransition Transition => InMemoryAiReapDb.Rows(Db.ProjectStageTransitions).Single();

        public CapturingLogger<ProjectLifecycleService> Log { get; } = new();

        public ProjectLifecycleService Lifecycle(User user) =>
            new(Db, user, new OpenAccess(), new SwitchableEvidence(new LifecycleEvidenceService(Db, _roles, user)) { Failure = EvidenceFailure },
                new LifecycleNotifications(Db, _roles), new ExitCheckGuidanceService(Db, _roles, user), Log);

        public Task<StageTransitionResponse> SubmitAsync(User? user = null, string? overrideReason = null) =>
            Lifecycle(user ?? Dev).AdvanceAsync(Project.Id, new AdvanceStageRequest(ProjectLifecyclePolicy.Next(Project.Status)!.Value, null, overrideReason));

        public Task<StageTransitionResponse> ApproveAsync(Guid id, User? user = null, string? overrideReason = null) =>
            Lifecycle(user ?? Reviewer).DecideAsync(id, new DecideStageTransitionRequest(true, null, overrideReason));

        public Task<StageTransitionResponse> RejectAsync(Guid id, User? user = null) =>
            Lifecycle(user ?? Reviewer).DecideAsync(id, new DecideStageTransitionRequest(false, "Not ready."));
    }

    private static List<SdlcCheck> Checks(string json) =>
        JsonSerializer.Deserialize<List<SdlcCheck>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // ---- approval -----------------------------------------------------------------------------------

    [Fact]
    public async Task Approval_keeps_the_submission_snapshot_and_records_its_own_evaluation()
    {
        // Submitted over a failing check (Administrator override), fixed before approval.
        var w = new Workspace(ProjectStatus.Design, "unverified");
        var submitted = await w.SubmitAsync(Workspace.Admin, "FR-001 quote accepted for now.");
        var submissionSnapshot = w.Transition.EvidenceSnapshotJson;
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
        Assert.Null(submitted.DecisionEvidence);

        w.Fr.DataJson = Fr("verified");
        var approved = await w.ApproveAsync(submitted.Id);

        Assert.Equal(StageTransitionState.Completed, approved.State);
        Assert.Equal(submissionSnapshot, w.Transition.EvidenceSnapshotJson);              // untouched
        Assert.False(approved.Evidence.Single(c => c.Label == SourceEvidenceLabel).Passed); // as submitted
        Assert.Equal(submitted.Evidence, approved.Evidence);

        Assert.NotNull(w.Transition.DecisionEvidenceSnapshotJson);
        Assert.NotNull(approved.DecisionEvidence);
        Assert.All(approved.DecisionEvidence!, c => Assert.True(c.Passed, c.Label));         // as decided
        Assert.Equal(Checks(w.Transition.DecisionEvidenceSnapshotJson!), approved.DecisionEvidence);
    }

    [Fact]
    public async Task The_submission_override_scope_still_reads_the_submission_snapshot()
    {
        // F3/D2 - an approval reads its authorization from the submission snapshot, which D3 no longer overwrites.
        var w = new Workspace(ProjectStatus.Design, "unverified");
        var submitted = await w.SubmitAsync(Workspace.Admin, "Accepted.");
        var approved = await w.ApproveAsync(submitted.Id);

        Assert.Equal(StageTransitionState.Completed, approved.State);
        Assert.Equal(approved.Evidence, approved.DecisionEvidence); // unchanged evidence: the same checks, twice
        Assert.Contains("\"failures\"", w.Transition.EvidenceSnapshotJson);
        Assert.Contains("\"failures\"", w.Transition.DecisionEvidenceSnapshotJson);
    }

    [Fact]
    public async Task A_refused_approval_records_no_decision_snapshot()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        var submissionSnapshot = w.Transition.EvidenceSnapshotJson;

        w.Fr.DataJson = Fr("unverified");
        await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.ApproveAsync(submitted.Id));

        Assert.Equal(StageTransitionState.Pending, w.Transition.State);
        Assert.Equal(submissionSnapshot, w.Transition.EvidenceSnapshotJson);
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
    }

    // ---- rejection ----------------------------------------------------------------------------------

    [Fact]
    public async Task Rejection_keeps_the_submission_snapshot_and_records_the_evidence_at_rejection()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        var submissionSnapshot = w.Transition.EvidenceSnapshotJson;

        w.Fr.DataJson = Fr("unverified"); // evidence now fails: recorded, but it does not matter to a rejection
        var rejected = await w.RejectAsync(submitted.Id);

        Assert.Equal(StageTransitionState.Rejected, rejected.State);
        Assert.Equal(submissionSnapshot, w.Transition.EvidenceSnapshotJson);
        Assert.All(rejected.Evidence, c => Assert.True(c.Passed, c.Label));
        Assert.False(rejected.DecisionEvidence!.Single(c => c.Label == SourceEvidenceLabel).Passed);
        Assert.Equal(ProjectStatus.Design, w.Project.Status);
    }

    [Fact]
    public async Task Rejection_by_an_Administrator_with_failing_evidence_needs_no_override()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        w.Fr.DataJson = Fr("unverified");

        var rejected = await w.RejectAsync(submitted.Id, Workspace.Admin2);

        Assert.Equal(StageTransitionState.Rejected, rejected.State);
        Assert.False(rejected.IsOverride);
        Assert.NotNull(rejected.DecisionEvidence);
    }

    [Fact]
    public async Task Rejection_is_not_blocked_when_the_evidence_cannot_be_evaluated()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        var submissionSnapshot = w.Transition.EvidenceSnapshotJson;

        w.EvidenceFailure = new InvalidOperationException("evidence store unavailable");
        var rejected = await w.RejectAsync(submitted.Id);

        Assert.Equal(StageTransitionState.Rejected, rejected.State);
        Assert.Equal("Not ready.", rejected.DecisionReason);
        Assert.Equal(submissionSnapshot, w.Transition.EvidenceSnapshotJson);
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson); // not recorded, rather than made up
        Assert.Null(rejected.DecisionEvidence);
    }

    [Fact]
    public async Task A_rejection_whose_evidence_cannot_be_evaluated_logs_a_warning_with_identifiers_only()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        var failure = new InvalidOperationException("evidence store unavailable");

        w.EvidenceFailure = failure;
        var rejected = await w.RejectAsync(submitted.Id);

        Assert.Equal(StageTransitionState.Rejected, rejected.State);
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
        var entry = Assert.Single(w.Log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains(submitted.Id.ToString(), entry.Message);
        Assert.Contains(w.Project.Id.ToString(), entry.Message);
        Assert.Contains("Design", entry.Message);
        // No requirement or evidence content: not the quote, the FR's data, the rejection reason or the note.
        Assert.DoesNotContain("the clerk records every booking", entry.Message);
        Assert.DoesNotContain(w.Fr.DataJson, entry.Message);
        Assert.DoesNotContain("Booking stored", entry.Message);
        Assert.DoesNotContain("Not ready.", entry.Message);
    }

    [Fact]
    public async Task A_rejection_whose_evidence_is_evaluated_logs_nothing()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();

        await w.RejectAsync(submitted.Id);

        Assert.Empty(w.Log.Entries);
    }

    [Fact]
    public async Task A_cancelled_rejection_is_still_cancelled()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();

        w.EvidenceFailure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => w.RejectAsync(submitted.Id));

        Assert.Equal(StageTransitionState.Pending, w.Transition.State);
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
    }

    [Fact]
    public async Task Rejection_still_needs_a_reason_and_the_submitter_still_cannot_decide()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            w.Lifecycle(Workspace.Reviewer).DecideAsync(submitted.Id, new DecideStageTransitionRequest(false, "  ")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.RejectAsync(submitted.Id, Workspace.Dev));

        Assert.Equal(StageTransitionState.Pending, w.Transition.State);
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
    }

    // ---- rows without a decision snapshot -----------------------------------------------------------

    [Fact]
    public async Task Withdrawal_leaves_the_decision_snapshot_null()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        var submissionSnapshot = w.Transition.EvidenceSnapshotJson;

        var withdrawn = await w.Lifecycle(Workspace.Dev).WithdrawAsync(submitted.Id, "Not yet.");

        Assert.Equal(StageTransitionState.Withdrawn, withdrawn.State);
        Assert.Equal(submissionSnapshot, w.Transition.EvidenceSnapshotJson);
        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
        Assert.Null(withdrawn.DecisionEvidence);
    }

    [Fact]
    public async Task Direct_advances_and_rollbacks_leave_the_decision_snapshot_null()
    {
        var w = new Workspace(ProjectStatus.Implementation);
        var advanced = await w.SubmitAsync(Workspace.Admin, "Standing in for the Developer.");
        Assert.Equal(StageTransitionState.Completed, advanced.State);
        Assert.Null(advanced.DecisionEvidence);

        var rolledBack = await w.Lifecycle(Workspace.Admin).RollbackAsync(w.Project.Id, new RollbackStageRequest(ProjectStatus.Implementation, "Reopened."));
        Assert.Null(rolledBack.DecisionEvidence);
        Assert.All(InMemoryAiReapDb.Rows(w.Db.ProjectStageTransitions), t => Assert.Null(t.DecisionEvidenceSnapshotJson));
    }

    [Theory]
    [InlineData(StageTransitionState.Completed)]
    [InlineData(StageTransitionState.Rejected)]
    public async Task Legacy_decided_rows_are_returned_as_stored_and_not_backfilled(StageTransitionState state)
    {
        // A row decided before D3: one snapshot only, no decision snapshot. For an approved row that
        // snapshot is the approval-time one (the submission's is lost); for a rejected row it is the
        // submission's. Either way it is shown as stored, and nothing is invented for the other field.
        var w = new Workspace(ProjectStatus.Design);
        var legacy = new ProjectStageTransition
        {
            Id = Guid.NewGuid(), ProjectId = w.Project.Id, Sequence = 1, Kind = StageTransitionKind.Advance, State = state,
            FromStatus = ProjectStatus.Analysis, ToStatus = ProjectStatus.Design, RequestedByUserId = "ba", RequestedByRole = Roles.BusinessAnalyst,
            RequestedAt = DateTime.UtcNow.AddDays(-2), DecidedByUserId = "rev", DecidedByRole = Roles.Reviewer, DecidedAt = DateTime.UtcNow.AddDays(-1),
            DecisionReason = state == StageTransitionState.Rejected ? "Not ready." : null,
            EvidenceSnapshotJson = "[{\"label\":\"Legacy check\",\"passed\":true,\"detail\":\"as stored\",\"key\":\"legacy.key\"}]"
        };
        w.Db.ProjectStageTransitions.Add(legacy);
        var stored = legacy.EvidenceSnapshotJson;

        var history = await w.Lifecycle(Workspace.Dev).GetHistoryAsync(w.Project.Id);
        var state2 = await w.Lifecycle(Workspace.Dev).GetStateAsync(w.Project.Id);

        var row = Assert.Single(history);
        Assert.Equal(new[] { new SdlcCheck("Legacy check", true, "as stored") }, row.Evidence);
        Assert.Null(row.DecisionEvidence);
        Assert.Null(Assert.Single(state2.History).DecisionEvidence);
        Assert.Equal(stored, legacy.EvidenceSnapshotJson);
        Assert.Null(legacy.DecisionEvidenceSnapshotJson);
        Assert.Equal(0, w.Db.SaveCount);
    }

    // ---- SubmissionEvidenceUnavailable --------------------------------------------------------------
    // An availability flag, not a "legacy row" marker: it is set only where the stored snapshot is known
    // not to be the submission's (an approved gated request with no decision snapshot), never merely
    // because the decision snapshot is null.

    private static ProjectStageTransition StoredRow(Workspace w, StageTransitionState state, ProjectStatus from, ProjectStatus to,
        string requestedBy = "dev", string? decidedBy = "rev", string? decisionSnapshot = null) => new()
    {
        Id = Guid.NewGuid(), ProjectId = w.Project.Id, Sequence = 1, Kind = StageTransitionKind.Advance, State = state,
        FromStatus = from, ToStatus = to, RequestedByUserId = requestedBy, RequestedByRole = Roles.Developer,
        RequestedAt = DateTime.UtcNow.AddDays(-2), DecidedByUserId = decidedBy, DecidedByRole = decidedBy is null ? null : Roles.Reviewer,
        DecidedAt = decidedBy is null ? null : DateTime.UtcNow.AddDays(-1),
        DecisionReason = state == StageTransitionState.Rejected ? "Not ready." : null,
        EvidenceSnapshotJson = "[{\"label\":\"Stored check\",\"passed\":true,\"detail\":\"as stored\",\"key\":\"stored.key\"}]",
        DecisionEvidenceSnapshotJson = decisionSnapshot
    };

    [Fact]
    public async Task An_approved_gated_request_without_a_decision_snapshot_reports_its_submission_evidence_unavailable()
    {
        // The shape an approval made before D3 left: one snapshot (approval-time), no decision snapshot.
        var w = new Workspace(ProjectStatus.Approved);
        var row = StoredRow(w, StageTransitionState.Completed, ProjectStatus.Design, ProjectStatus.Approved);
        w.Db.ProjectStageTransitions.Add(row);
        var stored = row.EvidenceSnapshotJson;

        var history = Assert.Single(await w.Lifecycle(Workspace.Dev).GetHistoryAsync(w.Project.Id));
        var state = Assert.Single((await w.Lifecycle(Workspace.Dev).GetStateAsync(w.Project.Id)).History);

        Assert.True(history.SubmissionEvidenceUnavailable);
        Assert.True(state.SubmissionEvidenceUnavailable);
        Assert.Equal(new[] { new SdlcCheck("Stored check", true, "as stored") }, history.Evidence); // shown as stored
        Assert.Null(history.DecisionEvidence);                                                       // nothing invented
        Assert.Equal(stored, row.EvidenceSnapshotJson);                                              // not backfilled
        Assert.Null(row.DecisionEvidenceSnapshotJson);
        Assert.Equal(0, w.Db.SaveCount);
    }

    [Fact]
    public async Task A_null_decision_snapshot_alone_does_not_report_submission_evidence_unavailable()
    {
        // A rejection whose evaluation failed has the same null decision snapshot as a rejection made
        // before D3; both still hold their submission snapshot, so neither is flagged.
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        w.EvidenceFailure = new InvalidOperationException("evidence store unavailable");
        var rejected = await w.RejectAsync(submitted.Id);

        Assert.Null(w.Transition.DecisionEvidenceSnapshotJson);
        Assert.False(rejected.SubmissionEvidenceUnavailable);
        Assert.Equal(submitted.Evidence, rejected.Evidence);

        var w2 = new Workspace(ProjectStatus.Design);
        w2.Db.ProjectStageTransitions.Add(StoredRow(w2, StageTransitionState.Rejected, ProjectStatus.Design, ProjectStatus.Approved));
        Assert.False(Assert.Single(await w2.Lifecycle(Workspace.Dev).GetHistoryAsync(w2.Project.Id)).SubmissionEvidenceUnavailable);
    }

    [Fact]
    public async Task Rows_this_code_writes_never_report_submission_evidence_unavailable()
    {
        // Approved (D3 decision snapshot recorded), pending, withdrawn, rejected.
        var approved = new Workspace(ProjectStatus.Design);
        var submitted = await approved.SubmitAsync();
        Assert.False(submitted.SubmissionEvidenceUnavailable);
        var decided = await approved.ApproveAsync(submitted.Id);
        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.NotNull(decided.DecisionEvidence);
        Assert.False(decided.SubmissionEvidenceUnavailable);
        Assert.False(Assert.Single(await approved.Lifecycle(Workspace.Dev).GetHistoryAsync(approved.Project.Id)).SubmissionEvidenceUnavailable);

        var withdrawn = new Workspace(ProjectStatus.Design);
        var pending = await withdrawn.SubmitAsync();
        Assert.False((await withdrawn.Lifecycle(Workspace.Dev).WithdrawAsync(pending.Id, "Not yet.")).SubmissionEvidenceUnavailable);

        var rejected = new Workspace(ProjectStatus.Design);
        Assert.False((await rejected.RejectAsync((await rejected.SubmitAsync()).Id)).SubmissionEvidenceUnavailable);

        // Direct advances and rollbacks have no decision snapshot either, and are not gated requests.
        var direct = new Workspace(ProjectStatus.Implementation);
        var advanced = await direct.SubmitAsync(Workspace.Admin, "Standing in for the Developer.");
        Assert.Null(advanced.DecisionEvidence);
        Assert.False(advanced.SubmissionEvidenceUnavailable);
        Assert.False((await direct.Lifecycle(Workspace.Admin).RollbackAsync(direct.Project.Id, new RollbackStageRequest(ProjectStatus.Implementation, "Reopened."))).SubmissionEvidenceUnavailable);
    }

    [Theory]
    [InlineData(ProjectStatus.Implementation, "rev")] // not a gated stage: a direct advance's own snapshot
    [InlineData(ProjectStatus.Design, "dev")]         // decided by its requester: not a gated decision
    [InlineData(ProjectStatus.Design, null)]          // never decided
    public void Only_a_gated_approval_by_another_user_can_report_submission_evidence_unavailable(ProjectStatus from, string? decidedBy)
    {
        var w = new Workspace(ProjectStatus.Design);
        var row = StoredRow(w, StageTransitionState.Completed, from, ProjectLifecyclePolicy.Next(from)!.Value, decidedBy: decidedBy);

        Assert.False(ProjectLifecycleService.ToResponse(row).SubmissionEvidenceUnavailable);
        row.DecisionEvidenceSnapshotJson = "[]";
        Assert.False(ProjectLifecycleService.ToResponse(row).SubmissionEvidenceUnavailable);
    }

    [Fact]
    public void A_gated_approval_with_a_decision_snapshot_has_its_submission_evidence()
    {
        var w = new Workspace(ProjectStatus.Approved);
        var row = StoredRow(w, StageTransitionState.Completed, ProjectStatus.Testing, ProjectLifecyclePolicy.Next(ProjectStatus.Testing)!.Value,
            decisionSnapshot: "[{\"label\":\"Decided\",\"passed\":true,\"detail\":\"d\",\"key\":\"k\"}]");

        Assert.False(ProjectLifecycleService.ToResponse(row).SubmissionEvidenceUnavailable);
        row.DecisionEvidenceSnapshotJson = null;
        Assert.True(ProjectLifecycleService.ToResponse(row).SubmissionEvidenceUnavailable);
    }

    // ---- what the API returns -----------------------------------------------------------------------

    [Fact]
    public async Task Neither_evidence_field_exposes_failure_identities_keys_or_guidance()
    {
        var w = new Workspace(ProjectStatus.Design, "unverified");
        var submitted = await w.SubmitAsync(Workspace.Admin, "Accepted.");
        var rejected = await w.RejectAsync(submitted.Id);

        // Stored server-side (the decision snapshot is in the same format as the submission one)...
        Assert.Contains("\"failures\"", w.Transition.DecisionEvidenceSnapshotJson);
        Assert.Contains("\"key\"", w.Transition.DecisionEvidenceSnapshotJson);

        foreach (var response in new[] { rejected, Assert.Single(await w.Lifecycle(Workspace.Dev).GetHistoryAsync(w.Project.Id)) })
        {
            Assert.NotNull(response.DecisionEvidence);
            Assert.All(response.Evidence.Concat(response.DecisionEvidence!), c => Assert.IsType<SdlcCheck>(c));
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain(w.Fr.Id.ToString("N"), json);
            Assert.DoesNotContain(w.Fr.Id.ToString(), json);
            Assert.DoesNotContain("failures", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"key\"", json);
            Assert.DoesNotContain("guidance", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("blockers", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Existing_positional_callers_still_compile_and_default_to_no_decision_evidence()
    {
        var response = new StageTransitionResponse(
            Guid.NewGuid(), Guid.NewGuid(), 1, StageTransitionKind.Advance, StageTransitionState.Pending, ProjectStatus.Design, ProjectStatus.Approved,
            "dev", Roles.Developer, DateTime.UtcNow, null, null, null, null, null, false, null, [], []);

        Assert.Null(response.DecisionEvidence);
        Assert.False(response.SubmissionEvidenceUnavailable);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"decisionEvidence\":null", json);
        Assert.Contains("\"submissionEvidenceUnavailable\":false", json);
    }
}
