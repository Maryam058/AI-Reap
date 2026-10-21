using System.Text.Json;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Application.Traceability;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Xunit;

namespace AiReap.Tests;

// G3 — evidence-aware approval: the Design and Testing exit checks and artifact approval refuse an
// FR/NFR whose stored evidence is unverified, an explicit claim without a quote, or an unconfirmed /
// unsupported NFR target; legacy records and derived/assumed ones are named, not blocked. F3 — an
// override given at submission covers only the failures it was given over.
//
// The real services over InMemoryAiReapDb: no AI provider, no SQL Server, no network. LINQ runs as
// LINQ-to-objects here, so EF translation of these queries is not covered by these tests.
[Trait("Gap", "G3")]
public class EvidenceApprovalGateTests
{
    private const string QuoteText = "QUOTE-SENTINEL-7Q the clerk records every booking";

    // ---- payloads -------------------------------------------------------------------------------

    private static EvidenceQuotePayload Quote(string status) =>
        new() { From = "srs", Quote = QuoteText, SourceId = "FR-001", Status = status, Reason = status == "verified" ? null : "not_found", MatchCount = status == "verified" ? 1 : 0 };

    private static string Fr(string? evidenceStatus = "verified", string? origin = "explicit", bool legacy = false) =>
        JsonSerializer.Serialize(new FunctionalRequirementPayload
        {
            Actor = "Clerk",
            ExpectedResult = "Booking stored.",
            RequirementOrigin = origin,
            Evidence = legacy || evidenceStatus is null or "none" ? [] : [Quote(evidenceStatus)],
            EvidenceStatus = legacy ? null : evidenceStatus,
            EvidenceCheck = legacy ? null : RequirementEvidence.CheckVersion
        });

    private static string Nfr(string? evidenceStatus = "verified", string? target = null, string? assumption = null, string? targetSupport = null, string? origin = "explicit") =>
        JsonSerializer.Serialize(new NonFunctionalRequirementPayload
        {
            Category = "Performance",
            Description = "Search responds quickly.",
            TargetValue = target,
            AssumptionStatus = assumption,
            ConfirmedByUserId = assumption == NfrAssumption.Confirmed ? "ba2" : null,
            RequirementOrigin = origin,
            Evidence = evidenceStatus is null or "none" ? [] : [Quote(evidenceStatus)],
            EvidenceStatus = evidenceStatus,
            EvidenceCheck = RequirementEvidence.CheckVersion,
            TargetOrigin = targetSupport is null ? null : "stated",
            TargetSupport = targetSupport
        });

    // ---- fakes ----------------------------------------------------------------------------------

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

    private sealed class NoImpact : IImpactAnalysisService
    {
        public Task<ImpactAnalysisResult?> AnalyzeAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StageNoticesForChangeAsync(Artifact changed, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForProjectAsync(Guid projectId, bool includeAcknowledged, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ImpactNoticeResponse>> GetNoticesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImpactNoticeResponse?> AcknowledgeAsync(Guid noticeId, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---- workspace: a project whose Design and Testing exit checks all pass ----------------------

    private sealed class Workspace
    {
        public static readonly User Dev = new("dev", Roles.Developer);
        public static readonly User Reviewer = new("rev", Roles.Reviewer);
        public static readonly User Qa = new("qa", Roles.QA);
        public static readonly User Admin = new("admin", Roles.Administrator);
        public static readonly User Ba = new("ba", Roles.BusinessAnalyst);
        public static readonly User DevReviewer = new("devrev", Roles.Developer, Roles.Reviewer);

        private readonly RoleDirectory _roles;

        public InMemoryAiReapDb Db { get; } = new();
        public Project Project { get; }
        public Guid SourceId { get; } = Guid.NewGuid();
        public Artifact Fr { get; }
        public Artifact Design { get; }

        public Workspace(ProjectStatus status, string? frJson = null)
        {
            var users = new[] { Dev, Reviewer, Qa, Admin, Ba, DevReviewer };
            _roles = new RoleDirectory(new()
            {
                ["dev"] = [Roles.Developer], ["rev"] = [Roles.Reviewer], ["qa"] = [Roles.QA], ["admin"] = [Roles.Administrator],
                ["ba"] = [Roles.BusinessAnalyst], ["devrev"] = [Roles.Developer, Roles.Reviewer]
            });
            Project = new Project { Id = Guid.NewGuid(), Name = "G3 offline", Status = status, Objectives = "o", BusinessProblem = "p", Scope = "s", CreatedByUserId = "ba" };
            Db.Projects.Add(Project);
            foreach (var user in users)
            {
                Db.ProjectMembers.Add(new ProjectMember { Id = Guid.NewGuid(), ProjectId = Project.Id, UserId = user.UserId, AddedByUserId = "ba", AddedAt = DateTime.UtcNow });
            }

            Fr = Add(ArtifactType.FunctionalRequirement, "FR-001", frJson ?? Fr());
            var story = Add(ArtifactType.UserStory, "US-001");
            var criterion = Add(ArtifactType.AcceptanceCriterion, "AC-001");
            Design = Add(ArtifactType.DesignArtifact, "DES-001");
            var api = Add(ArtifactType.ApiSpecification, "API-001");
            var entity = Add(ArtifactType.DataEntity, "DE-001");
            var task = Add(ArtifactType.ImplementationTask, "TASK-001");
            var test = Add(ArtifactType.TestCase, "TC-001");

            Link(story, Fr, RelationshipType.DerivedFrom);
            Link(criterion, story, RelationshipType.DerivedFrom);
            Link(task, Fr, RelationshipType.Implements);
            Link(Fr, test, RelationshipType.TestedBy);
            foreach (var designed in new[] { Design, api, entity })
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

        public Artifact Add(ArtifactType type, string code, string dataJson = "{}", ArtifactStatus status = ArtifactStatus.Approved)
        {
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(), ProjectId = Project.Id, ArtifactType = type, Code = code, Title = code, Status = status,
                Origin = ArtifactOrigin.Ai, RequirementSourceId = SourceId, DataJson = dataJson, CurrentVersion = 1,
                ApprovedVersion = ArtifactStatusTransitions.IsApprovedOrLater(status) ? 1 : null,
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

        public void OpenNotice(Artifact artifact) =>
            Db.ArtifactImpactNotices.Add(new ArtifactImpactNotice { Id = Guid.NewGuid(), ProjectId = Project.Id, AffectedArtifactId = artifact.Id, SourceArtifactId = Fr.Id, CreatedAt = DateTime.UtcNow });

        public LifecycleEvidenceService Evidence(User user) => new(Db, _roles, user);

        public ProjectLifecycleService Lifecycle(User user) =>
            new(Db, user, new OpenAccess(), Evidence(user), new LifecycleNotifications(Db, _roles), new ExitCheckGuidanceService(Db, _roles, user),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectLifecycleService>.Instance);

        public ArtifactService Artifacts(User user) => new(Db, user, new OpenAccess(), new NoImpact());

        public async Task<CheckEvidence> SourceCheckAsync(User user) =>
            (await Evidence(user).GetDetailedAsync(Project.Id)).Single(s => s.Stage == Project.Status).Checks.Single(c => c.Key == ExitCheckKeys.SourceEvidence);

        // The stage owner's submission: a Developer for Design; QA (with the sign-off note) for Testing.
        public Task<StageTransitionResponse> SubmitAsync(User? user = null, string? overrideReason = null) =>
            Lifecycle(user ?? Owner).AdvanceAsync(Project.Id, new AdvanceStageRequest(ProjectLifecyclePolicy.Next(Project.Status)!.Value, SignOffNote, overrideReason));

        public Task<StageTransitionResponse> DecideAsync(Guid transitionId, User user, string? overrideReason = null) =>
            Lifecycle(user).DecideAsync(transitionId, new DecideStageTransitionRequest(true, null, overrideReason));

        private User Owner => Project.Status == ProjectStatus.Testing ? Qa : Dev;
        private string? SignOffNote => Project.Status == ProjectStatus.Testing ? "QA sign-off: all passed." : null;
    }

    public static TheoryData<ProjectStatus> Gates => new() { ProjectStatus.Design, ProjectStatus.Testing };

    private static void AssertNoQuote(object value) =>
        Assert.DoesNotContain("QUOTE-SENTINEL", JsonSerializer.Serialize(value), StringComparison.Ordinal);

    // ---- lifecycle gates ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task Verified_evidence_passes_the_gate_when_every_other_check_passes(ProjectStatus gate)
    {
        var w = new Workspace(gate);

        var state = await w.Lifecycle(Workspace.Reviewer).GetStateAsync(w.Project.Id);
        Assert.True(state.ReadyToAdvance, string.Join(" | ", state.ExitChecks.Where(c => !c.Passed).Select(c => $"{c.Label}: {c.Detail}")));
        Assert.Contains(state.ExitChecks, c => c.Label == "Requirement source evidence and NFR targets accepted" && c.Passed);

        var submitted = await w.SubmitAsync();
        var decided = await w.DecideAsync(submitted.Id, Workspace.Reviewer);

        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.False(decided.IsOverride);
        Assert.Equal(ProjectLifecyclePolicy.Next(gate), w.Project.Status);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task An_unverified_requirement_blocks_submission_without_exposing_its_quote(ProjectStatus gate)
    {
        var w = new Workspace(gate, Fr("unverified"));

        var check = await w.SourceCheckAsync(Workspace.Reviewer);
        Assert.False(check.Check.Passed);
        Assert.Equal(BlockerReasons.EvidenceUnverified, Assert.Single(check.Blockers).Reason);

        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.SubmitAsync());
        var failing = Assert.Single(refused.FailingChecks);
        Assert.Contains("FR-001", failing.Detail);
        Assert.Equal(BlockerReasons.EvidenceUnverified, Assert.Single(failing.Guidance!.Blockers).Reason);
        AssertNoQuote(refused.FailingChecks);
        AssertNoQuote(refused.Message);
        Assert.Equal(gate, w.Project.Status);
        Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ProjectStageTransitions));
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task Evidence_that_becomes_unverified_after_submission_blocks_the_decision(ProjectStatus gate)
    {
        var w = new Workspace(gate);
        var submitted = await w.SubmitAsync();

        w.Fr.DataJson = Fr("unverified");
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        Assert.Equal("Requirement source evidence and NFR targets accepted", Assert.Single(refused.FailingChecks).Label);
        AssertNoQuote(refused.FailingChecks);
        Assert.Equal(gate, w.Project.Status);
        Assert.Equal(StageTransitionState.Pending, InMemoryAiReapDb.Rows(w.Db.ProjectStageTransitions).Single().State);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task Evidence_that_becomes_verified_after_submission_passes_the_decision(ProjectStatus gate)
    {
        var w = new Workspace(gate);
        var submitted = await w.SubmitAsync();
        w.Fr.DataJson = Fr("unverified");
        await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        w.Fr.DataJson = Fr("verified");
        var decided = await w.DecideAsync(submitted.Id, Workspace.Reviewer);
        Assert.Equal(StageTransitionState.Completed, decided.State);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task An_explicit_requirement_without_a_quote_blocks(ProjectStatus gate)
    {
        var w = new Workspace(gate, Fr("none", origin: "explicit"));

        var check = await w.SourceCheckAsync(Workspace.Reviewer);
        Assert.False(check.Check.Passed);
        Assert.Equal(BlockerReasons.EvidenceMissing, Assert.Single(check.Blockers).Reason);
        await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.SubmitAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stated-ish")]
    public async Task A_missing_or_unrecognized_origin_without_a_quote_is_treated_as_explicit_and_blocks(string? origin)
    {
        var w = new Workspace(ProjectStatus.Design, Fr("none", origin: origin));

        var check = await w.SourceCheckAsync(Workspace.Reviewer);
        Assert.Equal(BlockerReasons.EvidenceMissing, Assert.Single(check.Blockers).Reason);
    }

    [Theory]
    [InlineData("derived")]
    [InlineData("assumed")]
    [InlineData(" Assumed ")]
    public async Task A_derived_or_assumed_requirement_without_a_quote_passes_but_is_named_as_not_source_verified(string origin)
    {
        foreach (var gate in new[] { ProjectStatus.Design, ProjectStatus.Testing })
        {
            var w = new Workspace(gate, Fr("none", origin: origin));

            var check = await w.SourceCheckAsync(Workspace.Reviewer);
            Assert.True(check.Check.Passed);
            Assert.Contains("derived or assumed with no source quote (not source-verified): FR-001", check.Check.Detail);
            Assert.DoesNotContain("made before evidence checks", check.Check.Detail);

            var decided = await w.DecideAsync((await w.SubmitAsync()).Id, Workspace.Reviewer);
            Assert.Equal(StageTransitionState.Completed, decided.State);
            Assert.Equal(origin, JsonSerializer.Deserialize<FunctionalRequirementPayload>(w.Fr.DataJson)!.RequirementOrigin); // never rewritten
        }
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task A_legacy_record_without_an_evidence_check_is_named_but_does_not_block(ProjectStatus gate)
    {
        var json = Fr(legacy: true, origin: null);
        var w = new Workspace(gate, json);

        var check = await w.SourceCheckAsync(Workspace.Reviewer);
        Assert.True(check.Check.Passed);
        Assert.Contains("1 made before evidence checks (not verified): FR-001", check.Check.Detail);
        Assert.Equal(StageTransitionState.Completed, (await w.DecideAsync((await w.SubmitAsync()).Id, Workspace.Reviewer)).State);
        Assert.Equal(json, w.Fr.DataJson); // no status fabricated, nothing rewritten
    }

    [Fact]
    public async Task A_developer_is_told_about_hidden_legacy_and_blocked_requirements_without_their_codes()
    {
        var w = new Workspace(ProjectStatus.Design);
        w.Add(ArtifactType.FunctionalRequirement, "FR-002", Fr(legacy: true), ArtifactStatus.UnderReview);
        w.Add(ArtifactType.FunctionalRequirement, "FR-003", Fr("unverified"), ArtifactStatus.UnderReview);

        var check = await w.SourceCheckAsync(Workspace.Dev);

        Assert.False(check.Check.Passed);
        Assert.DoesNotContain("FR-002", check.Check.Detail);
        Assert.DoesNotContain("FR-003", check.Check.Detail);
        Assert.Contains("not listed until approved", check.Check.Detail);
        var state = await w.Lifecycle(Workspace.Dev).GetStateAsync(w.Project.Id);
        AssertNoQuote(state);
        var group = state.ExitChecks.Single(c => c.Guidance?.Key == ExitCheckKeys.SourceEvidence).Guidance!.Blockers.Single();
        Assert.Empty(group.Items);
        Assert.Equal(1, group.HiddenCount);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task Rejected_requirements_are_left_out_of_the_evidence_check(ProjectStatus gate)
    {
        var w = new Workspace(gate);
        w.Add(ArtifactType.FunctionalRequirement, "FR-009", Fr("unverified"), ArtifactStatus.Rejected);
        w.Add(ArtifactType.NonFunctionalRequirement, "NFR-009", Nfr("unverified", target: "2 s", assumption: NfrAssumption.Proposed), ArtifactStatus.Rejected);

        var check = await w.SourceCheckAsync(Workspace.Reviewer);
        Assert.True(check.Check.Passed);
        Assert.DoesNotContain("FR-009", check.Check.Detail);
        Assert.Equal(StageTransitionState.Completed, (await w.DecideAsync((await w.SubmitAsync()).Id, Workspace.Reviewer)).State);
    }

    [Theory]
    [InlineData("1 s", NfrAssumption.Proposed, null, BlockerReasons.TargetUnconfirmed)]
    [InlineData("1 s", NfrAssumption.Proposed, "not_supported", BlockerReasons.TargetNotSupported)]
    [InlineData("1 s", null, "not_supported", BlockerReasons.TargetNotSupported)]
    [InlineData("1 s", NfrAssumption.Confirmed, "not_supported", null)]
    [InlineData("1 s", NfrAssumption.Confirmed, "quote_found", null)]
    [InlineData(null, null, null, null)]
    public async Task Nfr_targets_must_be_confirmed_by_a_human_before_the_gate_passes(string? target, string? assumption, string? support, string? expected)
    {
        foreach (var gate in new[] { ProjectStatus.Design, ProjectStatus.Testing })
        {
            var w = new Workspace(gate);
            var json = Nfr(target: target, assumption: assumption, targetSupport: support);
            w.Add(ArtifactType.NonFunctionalRequirement, "NFR-001", json);

            var check = await w.SourceCheckAsync(Workspace.Reviewer);
            if (expected is null)
            {
                Assert.True(check.Check.Passed, check.Check.Detail);
                continue;
            }

            Assert.False(check.Check.Passed);
            Assert.Equal(expected, Assert.Single(check.Blockers).Reason);
            await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.SubmitAsync());
            Assert.Equal(NfrAssumption.IsConfirmed(json), NfrAssumption.IsConfirmed(w.Db.Artifacts.Single(a => a.Code == "NFR-001").DataJson)); // never auto-confirmed
        }
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public async Task An_administrator_override_with_a_reason_still_moves_a_blocked_stage(ProjectStatus gate)
    {
        var w = new Workspace(gate, Fr("unverified"));

        var submitted = await w.SubmitAsync(Workspace.Admin, "Quote mismatch accepted: wording differs in v2 of the SRS.");
        Assert.True(submitted.IsOverride);

        var decided = await w.DecideAsync(submitted.Id, Workspace.Reviewer);
        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.True(decided.IsOverride);
    }

    [Fact]
    public async Task Only_an_administrator_may_override_failing_evidence()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.SubmitAsync(Workspace.Dev, "I am sure it is fine."));

        w.Fr.DataJson = Fr("verified");
        var submitted = await w.SubmitAsync();
        w.Fr.DataJson = Fr("unverified");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer, "Reviewer override."));
        Assert.Equal(ProjectStatus.Design, w.Project.Status);
    }

    [Fact]
    public async Task Requester_and_approver_stay_separate_and_role_restricted()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync(Workspace.DevReviewer);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.DevReviewer));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.Dev));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.Qa));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.SubmitAsync(Workspace.Qa)); // QA does not own Design
        Assert.Equal(StageTransitionState.Completed, (await w.DecideAsync(submitted.Id, Workspace.Reviewer)).State);
    }

    // ---- F3: override carry-over ----------------------------------------------------------------

    [Fact]
    public async Task A_stand_in_override_made_while_every_check_passed_does_not_cover_a_later_failure()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync(Workspace.Admin, "Standing in for the Developer lead.");
        Assert.True(submitted.IsOverride);
        Assert.All(submitted.Evidence, c => Assert.True(c.Passed));

        w.Fr.DataJson = Fr("unverified");
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.Equal("Requirement source evidence and NFR targets accepted", Assert.Single(refused.FailingChecks).Label);
        Assert.Equal(ProjectStatus.Design, w.Project.Status);

        // A fresh Administrator override with a reason authorizes it, and is recorded.
        var admin2 = new User("admin2", Roles.Administrator);
        await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, admin2));
        var decided = await w.DecideAsync(submitted.Id, admin2, "Evidence re-checked by hand.");
        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.Equal("Standing in for the Developer lead. | Evidence re-checked by hand.", decided.OverrideReason);
    }

    [Fact]
    public async Task A_testing_override_given_over_the_missing_sign_off_does_not_cover_a_later_evidence_failure()
    {
        var w = new Workspace(ProjectStatus.Testing);
        var submitted = await w.SubmitAsync(Workspace.Admin, "QA lead absent; sign-off on file.");
        Assert.Equal(ProjectLifecycleService.SignOffLabel, Assert.Single(submitted.Evidence, c => !c.Passed).Label);

        w.Fr.DataJson = Fr("unverified");
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.Equal("Requirement source evidence and NFR targets accepted", Assert.Single(refused.FailingChecks).Label);

        w.Fr.DataJson = Fr("verified");
        Assert.Equal(StageTransitionState.Completed, (await w.DecideAsync(submitted.Id, Workspace.Reviewer)).State);
    }

    [Fact]
    public async Task A_stand_in_override_on_design_while_checks_pass_does_not_cover_a_new_failure()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync(Workspace.Admin, "Standing in for the Developer lead.");
        Assert.True(submitted.IsOverride);
        Assert.All(submitted.Evidence, c => Assert.True(c.Passed));

        w.OpenNotice(w.Design);
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.Equal("No open impact notices on design", Assert.Single(refused.FailingChecks).Label);
    }

    [Fact]
    public async Task An_override_given_over_failures_covers_exactly_those_failures_while_unchanged()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitAsync(Workspace.Admin, "Accepted: SRS v2 wording.");
        Assert.Contains(submitted.Evidence, c => !c.Passed);

        // Unchanged failure: covered by the submission's override, a Reviewer may approve.
        var decided = await w.DecideAsync(submitted.Id, Workspace.Reviewer);
        Assert.Equal(StageTransitionState.Completed, decided.State);
        Assert.Equal("Accepted: SRS v2 wording.", decided.OverrideReason);
    }

    [Fact]
    public async Task An_override_given_over_one_failure_does_not_cover_a_newly_failing_check()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitAsync(Workspace.Admin, "Accepted: SRS v2 wording.");

        w.OpenNotice(w.Design);
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));

        Assert.Equal("No open impact notices on design", Assert.Single(refused.FailingChecks).Label);
        Assert.Contains("did not cover", refused.Message);
        Assert.Equal(ProjectStatus.Design, w.Project.Status);
    }

    [Fact]
    public async Task An_override_given_over_a_failure_does_not_cover_that_check_failing_differently()
    {
        var w = new Workspace(ProjectStatus.Design, Fr("unverified"));
        var submitted = await w.SubmitAsync(Workspace.Admin, "Accepted: FR-001 wording.");

        w.Add(ArtifactType.FunctionalRequirement, "FR-002", Fr("unverified"));
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.Contains("FR-002", Assert.Single(refused.FailingChecks).Detail);

        // Reviewer cannot authorize it; an Administrator can, with a reason.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer, "Fine by me."));
        var decided = await w.DecideAsync(submitted.Id, new User("admin2", Roles.Administrator), "FR-002 accepted too.");
        Assert.Equal("Accepted: FR-001 wording. | FR-002 accepted too.", decided.OverrideReason);
    }

    [Fact]
    public async Task Without_a_submission_override_every_failure_still_needs_an_administrator()
    {
        var w = new Workspace(ProjectStatus.Design);
        var submitted = await w.SubmitAsync();
        Assert.False(submitted.IsOverride);

        w.Fr.DataJson = Fr("unverified");
        var refused = await Assert.ThrowsAsync<LifecycleEvidenceException>(() => w.DecideAsync(submitted.Id, Workspace.Reviewer));
        Assert.StartsWith("Design is not complete: 1 check(s) failing.", refused.Message);
    }

    // ---- artifact-level approval ----------------------------------------------------------------

    private static (Workspace W, Artifact Artifact) Pending(ArtifactType type, string json, ArtifactStatus status = ArtifactStatus.AiGenerated)
    {
        var w = new Workspace(ProjectStatus.Analysis);
        return (w, w.Add(type, type == ArtifactType.FunctionalRequirement ? "FR-050" : "NFR-050", json, status));
    }

    public static TheoryData<ArtifactType, string, string> RefusedApprovals => new()
    {
        { ArtifactType.FunctionalRequirement, Fr("unverified"), BlockerReasons.EvidenceUnverified },
        { ArtifactType.NonFunctionalRequirement, Nfr("unverified"), BlockerReasons.EvidenceUnverified },
        { ArtifactType.FunctionalRequirement, Fr("none", origin: "explicit"), BlockerReasons.EvidenceMissing },
        { ArtifactType.FunctionalRequirement, Fr("none", origin: null), BlockerReasons.EvidenceMissing },
        { ArtifactType.NonFunctionalRequirement, Nfr("none", origin: "explicit"), BlockerReasons.EvidenceMissing },
        { ArtifactType.NonFunctionalRequirement, Nfr(target: "200 ms", assumption: NfrAssumption.Proposed), BlockerReasons.TargetUnconfirmed },
        { ArtifactType.NonFunctionalRequirement, Nfr(target: "200 ms", assumption: NfrAssumption.Proposed, targetSupport: "not_supported"), BlockerReasons.TargetNotSupported },
    };

    [Theory]
    [MemberData(nameof(RefusedApprovals))]
    public async Task Artifact_approval_is_refused_and_changes_nothing(ArtifactType type, string json, string code)
    {
        foreach (var approver in new[] { Workspace.Reviewer, Workspace.Ba, Workspace.Admin })
        {
            var (w, artifact) = Pending(type, json);
            var before = (artifact.Status, artifact.DataJson, artifact.CurrentVersion, artifact.ApprovedVersion, artifact.Id, artifact.Code, artifact.UpdatedAt);

            // A comment is not an override, whoever sends it.
            var refused = await Assert.ThrowsAsync<ConflictException>(() =>
                w.Artifacts(approver).UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Approved, "Override: approve anyway.")));

            Assert.Contains(code, refused.Message);
            AssertNoQuote(refused.Message);
            Assert.Equal(before, (artifact.Status, artifact.DataJson, artifact.CurrentVersion, artifact.ApprovedVersion, artifact.Id, artifact.Code, artifact.UpdatedAt));
            Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactReviews).Where(r => r.ArtifactId == artifact.Id));
            Assert.Empty(InMemoryAiReapDb.Rows(w.Db.ArtifactVersions));
            Assert.Equal(0, w.Db.SaveCount);
        }
    }

    public static TheoryData<ArtifactType, string> AllowedApprovals => new()
    {
        { ArtifactType.FunctionalRequirement, Fr("verified") },
        { ArtifactType.FunctionalRequirement, Fr("none", origin: "derived") },
        { ArtifactType.FunctionalRequirement, Fr("none", origin: "assumed") },
        { ArtifactType.FunctionalRequirement, Fr(legacy: true, origin: null) },
        { ArtifactType.NonFunctionalRequirement, Nfr() },
        { ArtifactType.NonFunctionalRequirement, Nfr(target: "200 ms", assumption: NfrAssumption.Confirmed, targetSupport: "not_supported") },
    };

    [Theory]
    [MemberData(nameof(AllowedApprovals))]
    public async Task Artifact_approval_goes_ahead_when_the_policy_allows_it_without_touching_the_payload(ArtifactType type, string json)
    {
        var (w, artifact) = Pending(type, json);

        var approved = await w.Artifacts(Workspace.Reviewer).UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Approved, null));

        Assert.Equal(ArtifactStatus.Approved, approved!.Status);
        Assert.Equal(json, artifact.DataJson);
        Assert.Equal(1, artifact.ApprovedVersion);
    }

    [Fact]
    public async Task Rejecting_and_other_transitions_of_a_blocked_requirement_are_unchanged()
    {
        var (w, artifact) = Pending(ArtifactType.FunctionalRequirement, Fr("unverified"));
        var service = w.Artifacts(Workspace.Reviewer);

        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Rejected, null)));
        Assert.Equal(ArtifactStatus.UnderReview, (await service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.UnderReview, null)))!.Status);
        Assert.Equal(ArtifactStatus.Rejected, (await service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Rejected, "Not in the SRS.")))!.Status);
        Assert.Equal(ArtifactStatus.Draft, (await service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Draft, null)))!.Status);

        // An illegal transition is still refused by the state machine first.
        await Assert.ThrowsAsync<InvalidArtifactStatusTransitionException>(() => service.UpdateStatusAsync(artifact.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Approved, null)));
        Assert.Equal(Fr("unverified"), artifact.DataJson);
    }

    [Fact]
    public async Task Approving_other_artifact_types_ignores_evidence_fields()
    {
        var w = new Workspace(ProjectStatus.Analysis);
        var story = w.Add(ArtifactType.UserStory, "US-050", Fr("unverified"), ArtifactStatus.AiGenerated);

        var approved = await w.Artifacts(Workspace.Reviewer).UpdateStatusAsync(story.Id, new UpdateArtifactStatusRequest(ArtifactStatus.Approved, null));
        Assert.Equal(ArtifactStatus.Approved, approved!.Status);
    }

    [Fact]
    public void The_new_check_and_every_reason_have_catalogued_guidance()
    {
        Assert.True(ExitCheckCatalog.TryGet(ExitCheckKeys.SourceEvidence, out var guide));
        foreach (var reason in new[] { BlockerReasons.EvidenceUnverified, BlockerReasons.EvidenceMissing, BlockerReasons.TargetNotSupported, BlockerReasons.TargetUnconfirmed })
        {
            Assert.True(guide.Reasons.ContainsKey(reason), reason);
            Assert.False(string.IsNullOrWhiteSpace(guide.Reasons[reason].Instruction));
            Assert.Contains(reason, BlockerReasons.Order);
        }
    }

    // The same rule as ExitCheckGuidanceTests.Fix_roles_never_exceed_what_the_endpoint_allows (which
    // needs SQL Server for its fixture), for the new check only: reflection over the API assembly.
    [Fact]
    public void The_new_check_never_offers_a_fix_to_a_role_its_endpoint_refuses()
    {
        var api = typeof(Program).Assembly;
        Assert.True(ExitCheckCatalog.TryGet(ExitCheckKeys.SourceEvidence, out var guide));
        foreach (var (reason, reasonGuide) in guide.Reasons)
        {
            Assert.NotNull(reasonGuide.Endpoint);
            var parts = reasonGuide.Endpoint!.Split('.');
            var controller = api.GetTypes().Single(t => t.Name == parts[0]);
            var action = controller.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Single(m => m.Name == parts[1]);
            var roles = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(action)?.Roles;
            Assert.False(string.IsNullOrWhiteSpace(roles), reason);
            var allowed = roles!.Split(',', StringSplitOptions.TrimEntries);
            Assert.All(reasonGuide.FixRoles, r => Assert.Contains(r, allowed));
        }
    }
}
