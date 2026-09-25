using System.Text.Json.Nodes;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Microsoft.Data.SqlClient;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// §5 lifecycle alignment, phase 2: ProjectLifecycleService + lifecycle evidence, over a real database.
public class ProjectLifecycleServiceTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private Actor _ba = null!, _dev = null!, _reviewer = null!, _qa = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.ActorAsync(Roles.BusinessAnalyst);
        _dev = await _host.ActorAsync(Roles.Developer);
        _reviewer = await _host.ActorAsync(Roles.Reviewer);
        _qa = await _host.ActorAsync(Roles.QA);
        _admin = await _host.AdminAsync();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ---- helpers ---------------------------------------------------------------------------

    // A BA-created project whose Draft checks pass, with the other roles as members.
    private async Task<Guid> ProjectAsync(bool withScope = true)
    {
        var (status, body) = await _ba.Api.PostAsync("/api/projects", new
        {
            name = "Leave system",
            description = "d",
            businessProblem = "Leave is tracked on paper.",
            objectives = "Cut leave admin time",
            scope = withScope ? "Employees and managers" : null
        });
        Assert.Equal(201, status);
        var projectId = Id(body);
        foreach (var member in new[] { _dev, _reviewer, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba.Api, projectId, member.Api)).Status, 200, 201);
        }

        return Guid.Parse(projectId);
    }

    private Task SetStatusAsync(Guid projectId, ProjectStatus status) =>
        _host.ExecuteAsync($"UPDATE Projects SET Status = {(int)status} WHERE Id = '{projectId}'");

    private async Task<ProjectStatus> StatusAsync(Guid projectId) =>
        (ProjectStatus)await _host.ScalarAsync($"SELECT Status FROM Projects WHERE Id = '{projectId}'");

    private Task<int> TransitionCountAsync(Guid projectId) =>
        _host.ScalarAsync($"SELECT COUNT(*) FROM ProjectStageTransitions WHERE ProjectId = '{projectId}'");

    private async Task<List<Guid>> IdsAsync(string sql)
    {
        await using var conn = new SqlConnection(_host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private Task<List<Guid>> ArtifactsAsync(Guid projectId, params ArtifactType[] types) =>
        IdsAsync($"SELECT Id FROM Artifacts WHERE ProjectId = '{projectId}' AND ArtifactType IN ({string.Join(",", types.Select(t => (int)t))})");

    // Generates the full manual chain (FR -> story -> AC, design, tasks, tests) with the stub AI and
    // approves everything, the shortcut being a direct SQL approval: these tests are about the
    // lifecycle, and artifact approval has its own tests.
    private async Task FullChainAsync(Guid projectId, bool developerReview = true)
    {
        var (_, source) = await _ba.Api.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        var sourceId = Id(source);
        var (gs, gen) = await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        var (_, stories) = await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        foreach (var story in stories!.AsArray())
        {
            Assert.Equal(200, (await _ba.Api.PostAsync($"/api/artifacts/{Id(story)}/generate-acceptance-criteria")).Status);
        }

        foreach (var step in new[] { "generate-design", "generate-data-entities", "generate-api-specs", "generate-tasks" })
        {
            Assert.Equal(200, (await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        foreach (var fr in gen!["functionalRequirements"]!.AsArray())
        {
            Assert.Equal(200, (await _ba.Api.PostAsync($"/api/artifacts/{Id(fr)}/generate-test-cases")).Status);
        }

        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 3, ApprovedVersion = CurrentVersion WHERE ProjectId = '{projectId}' AND Status <> 4");

        if (developerReview)
        {
            await DeveloperReviewDesignAsync(projectId);
        }
    }

    private async Task DeveloperReviewDesignAsync(Guid projectId)
    {
        using var dev = _host.As(_dev);
        foreach (var id in await ArtifactsAsync(projectId, ArtifactType.DesignArtifact, ArtifactType.ApiSpecification, ArtifactType.DataEntity))
        {
            Assert.NotNull(await dev.Artifacts.AddCommentReviewAsync(id, "Technically sound."));
        }
    }

    private async Task<StageEvidence> StageAsync(Guid projectId, ProjectStatus stage)
    {
        using var s = _host.As(_admin);
        return (await s.Evidence.GetAsync(projectId)).Single(e => e.Stage == stage);
    }

    private static bool Check(StageEvidence stage, string label) => stage.Checks.Single(c => c.Label == label).Passed;

    private static async Task<Exception?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ---- history / baseline ----------------------------------------------------------------

    [Fact]
    public async Task A_new_project_starts_with_a_baseline_history_row()
    {
        var projectId = await ProjectAsync();
        using var s = _host.As(_ba);
        var history = await s.Lifecycle.GetHistoryAsync(projectId);

        var baseline = Assert.Single(history);
        Assert.Equal(StageTransitionKind.Baseline, baseline.Kind);
        Assert.Equal(StageTransitionState.Completed, baseline.State);
        Assert.Null(baseline.FromStatus);
        Assert.Equal(ProjectStatus.Draft, baseline.ToStatus);
        Assert.Equal(0, baseline.Sequence);
        Assert.Equal(_ba.Id, baseline.RequestedByUserId);
    }

    // ---- ownership / direct transitions ----------------------------------------------------

    [Fact]
    public async Task The_stage_owner_advances_a_direct_stage_and_the_move_is_recorded()
    {
        var projectId = await ProjectAsync();
        using var s = _host.As(_ba);

        var transition = await s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering, "Context captured."));

        Assert.Equal(ProjectStatus.RequirementsGathering, await StatusAsync(projectId));
        Assert.Equal(StageTransitionKind.Advance, transition.Kind);
        Assert.Equal(StageTransitionState.Completed, transition.State);
        Assert.Equal((ProjectStatus?)ProjectStatus.Draft, transition.FromStatus);
        Assert.Equal(Roles.BusinessAnalyst, transition.RequestedByRole);
        Assert.Equal(_ba.Id, transition.DecidedByUserId);
        Assert.False(transition.IsOverride);
        Assert.Equal(1, transition.Sequence);
        Assert.All(transition.Evidence, c => Assert.True(c.Passed));
        Assert.Contains(transition.Evidence, c => c.Label == "Business Analyst on the project");

        var history = await s.Lifecycle.GetHistoryAsync(projectId);
        Assert.Equal([StageTransitionKind.Baseline, StageTransitionKind.Advance], history.Select(h => h.Kind));
    }

    [Theory]
    [InlineData(Roles.Developer)]
    [InlineData(Roles.Reviewer)]
    [InlineData(Roles.QA)]
    public async Task Project_access_alone_does_not_let_another_role_advance(string role)
    {
        var projectId = await ProjectAsync();
        var member = role switch { Roles.Developer => _dev, Roles.Reviewer => _reviewer, _ => _qa };
        using var s = _host.As(member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering)));

        Assert.Equal(ProjectStatus.Draft, await StatusAsync(projectId));
        Assert.Equal(1, await TransitionCountAsync(projectId));
    }

    [Fact]
    public async Task A_business_analyst_who_is_not_a_member_is_refused()
    {
        var projectId = await ProjectAsync();
        var outsider = await _host.ActorAsync(Roles.BusinessAnalyst);
        using var s = _host.As(outsider);

        await Assert.ThrowsAsync<ProjectAccessDeniedException>(() =>
            s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering)));
    }

    [Theory]
    [InlineData(ProjectStatus.Analysis)]
    [InlineData(ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Draft)]
    public async Task Stages_cannot_be_skipped_or_repeated(ProjectStatus target)
    {
        var projectId = await ProjectAsync();
        using var s = _host.As(_ba);

        await Assert.ThrowsAsync<ConflictException>(() => s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(target)));
        Assert.Equal(ProjectStatus.Draft, await StatusAsync(projectId));
    }

    [Fact]
    public async Task Nothing_follows_completed()
    {
        var projectId = await ProjectAsync();
        await SetStatusAsync(projectId, ProjectStatus.Completed);
        using var s = _host.As(_admin);

        await Assert.ThrowsAsync<ConflictException>(() => s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Completed)));
    }

    [Fact]
    public async Task Failing_evidence_blocks_the_move_and_reports_the_failing_checks()
    {
        var projectId = await ProjectAsync(withScope: false);
        using var s = _host.As(_ba);

        var ex = await Assert.ThrowsAsync<LifecycleEvidenceException>(() =>
            s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering)));

        var failing = Assert.Single(ex.FailingChecks);
        Assert.Equal("Scope recorded", failing.Label);
        Assert.False(failing.Passed);
        Assert.Equal(ProjectStatus.Draft, await StatusAsync(projectId));
        Assert.Equal(1, await TransitionCountAsync(projectId));
    }

    [Fact]
    public async Task Draft_needs_an_active_business_analyst_member_and_an_administrator_may_advance_it()
    {
        // Created by an Administrator: its only member is the admin, so no BA is on it yet.
        var (_, body) = await _admin.Api.PostAsync("/api/projects", new
        {
            name = "Admin project", businessProblem = "p", objectives = "o", scope = "s"
        });
        var projectId = Guid.Parse(Id(body));
        Assert.False(Check(await StageAsync(projectId, ProjectStatus.Draft), "Business Analyst on the project"));

        // A deactivated BA doesn't count.
        var inactive = await _host.ActorAsync(Roles.BusinessAnalyst);
        await _host.AddMemberAsync(_admin.Api, projectId.ToString(), inactive.Api);
        Assert.Equal(200, (await _admin.Api.SendAsync(HttpMethod.Put, $"/api/users/{inactive.Id}/status", new { isActive = false })).Status);
        Assert.False(Check(await StageAsync(projectId, ProjectStatus.Draft), "Business Analyst on the project"));

        await _host.AddMemberAsync(_admin.Api, projectId.ToString(), _ba.Api);
        Assert.True((await StageAsync(projectId, ProjectStatus.Draft)).Passed);

        using var s = _host.As(_admin);
        var transition = await s.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering));
        Assert.Equal(Roles.Administrator, transition.RequestedByRole);
        Assert.Equal(ProjectStatus.RequirementsGathering, await StatusAsync(projectId));
    }

    // ---- administrator override ------------------------------------------------------------

    [Fact]
    public async Task Only_an_administrator_may_override_failing_evidence_and_only_with_a_reason()
    {
        var projectId = await ProjectAsync(withScope: false);

        using (var ba = _host.As(_ba))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ba.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering, OverrideReason: "trust me")));
        }

        using var admin = _host.As(_admin);
        await Assert.ThrowsAsync<LifecycleEvidenceException>(() =>
            admin.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering, OverrideReason: "   ")));
        Assert.Equal(ProjectStatus.Draft, await StatusAsync(projectId));

        var transition = await admin.Lifecycle.AdvanceAsync(projectId,
            new AdvanceStageRequest(ProjectStatus.RequirementsGathering, OverrideReason: "Scope agreed verbally; written up next sprint."));

        Assert.True(transition.IsOverride);
        Assert.Equal("Scope agreed verbally; written up next sprint.", transition.OverrideReason);
        Assert.Contains(transition.Evidence, c => c.Label == "Scope recorded" && !c.Passed);
        Assert.Equal(ProjectStatus.RequirementsGathering, await StatusAsync(projectId));
        Assert.Equal(1, await _host.ScalarAsync($"SELECT COUNT(*) FROM ProjectStageTransitions WHERE ProjectId = '{projectId}' AND IsOverride = 1"));
    }

    // ---- rollback --------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_is_administrator_only_needs_a_reason_goes_back_only_and_keeps_history()
    {
        var projectId = await ProjectAsync();
        using (var ba = _host.As(_ba))
        {
            await ba.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ba.Lifecycle.RollbackAsync(projectId, new RollbackStageRequest(ProjectStatus.Draft, "Wrong project")));
        }

        using var admin = _host.As(_admin);
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            admin.Lifecycle.RollbackAsync(projectId, new RollbackStageRequest(ProjectStatus.Draft, " ")));
        await Assert.ThrowsAsync<ConflictException>(() =>
            admin.Lifecycle.RollbackAsync(projectId, new RollbackStageRequest(ProjectStatus.RequirementsGathering, "same stage")));
        await Assert.ThrowsAsync<ConflictException>(() =>
            admin.Lifecycle.RollbackAsync(projectId, new RollbackStageRequest(ProjectStatus.Analysis, "forward is not a rollback")));
        Assert.Equal(ProjectStatus.RequirementsGathering, await StatusAsync(projectId));

        var rollback = await admin.Lifecycle.RollbackAsync(projectId, new RollbackStageRequest(ProjectStatus.Draft, "Objectives changed by the sponsor."));
        Assert.Equal(StageTransitionKind.Rollback, rollback.Kind);
        Assert.Equal("Objectives changed by the sponsor.", rollback.RequestNote);
        Assert.Equal(ProjectStatus.Draft, await StatusAsync(projectId));

        var history = await admin.Lifecycle.GetHistoryAsync(projectId);
        Assert.Equal([StageTransitionKind.Baseline, StageTransitionKind.Advance, StageTransitionKind.Rollback], history.Select(h => h.Kind));
        Assert.Equal([0, 1, 2], history.Select(h => h.Sequence));
    }

    // ---- gated: Design -> Approved ---------------------------------------------------------

    [Fact]
    public async Task Design_gate_submission_leaves_the_stage_and_only_a_different_reviewer_approves()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        await SetStatusAsync(projectId, ProjectStatus.Design);

        using var dev = _host.As(_dev);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _host.As(_ba).Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved)));

        var pending = await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved, "Design reviewed."));
        Assert.Equal(StageTransitionState.Pending, pending.State);
        Assert.Equal(Roles.Developer, pending.RequestedByRole);
        Assert.Null(pending.DecidedByUserId);
        Assert.Equal(ProjectStatus.Design, await StatusAsync(projectId));

        // One pending request per project.
        await Assert.ThrowsAsync<ConflictException>(() =>
            dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved)));

        // The developer is no approver; the requester can't approve even holding the Reviewer role.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            dev.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true)));
        using (var self = new ServicesAs(_host, new TestCurrentUser(_dev.Id, Roles.Developer, Roles.Reviewer)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                self.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true)));
        }

        using (var qa = _host.As(_qa))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                qa.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true)));
        }

        Assert.Equal(ProjectStatus.Design, await StatusAsync(projectId));

        using var reviewer = _host.As(_reviewer);
        var state = await reviewer.Lifecycle.GetStateAsync(projectId);
        Assert.True(state.Actions.CanDecide);
        Assert.False((await dev.Lifecycle.GetStateAsync(projectId)).Actions.CanDecide);
        Assert.True((await dev.Lifecycle.GetStateAsync(projectId)).Actions.CanWithdraw);

        var approved = await reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true, "Baseline approved."));
        Assert.Equal(StageTransitionState.Completed, approved.State);
        Assert.Equal(Roles.Reviewer, approved.DecidedByRole);
        Assert.Equal(_reviewer.Id, approved.DecidedByUserId);
        Assert.False(approved.IsOverride);
        var review = Assert.Single(approved.Reviews);
        Assert.Equal(ReviewDecision.Approved, review.Decision);
        Assert.Equal(ProjectStatus.Approved, await StatusAsync(projectId));

        await Assert.ThrowsAsync<ConflictException>(() =>
            reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true)));
    }

    [Fact]
    public async Task Developer_design_review_is_required_and_is_not_the_same_as_approval()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId, developerReview: false);
        await SetStatusAsync(projectId, ProjectStatus.Design);
        const string label = "Developer review of the current design";

        // Every design artifact is approved, and a Reviewer has commented: still no developer review.
        Assert.True(Check(await StageAsync(projectId, ProjectStatus.Design), "Solution design approved"));
        using (var reviewer = _host.As(_reviewer))
        {
            foreach (var id in await ArtifactsAsync(projectId, ArtifactType.DesignArtifact, ArtifactType.ApiSpecification, ArtifactType.DataEntity))
            {
                await reviewer.Artifacts.AddCommentReviewAsync(id, "Looks fine to me.");
            }
        }

        Assert.False(Check(await StageAsync(projectId, ProjectStatus.Design), label));

        using var dev = _host.As(_dev);
        var ex = await Assert.ThrowsAsync<LifecycleEvidenceException>(() =>
            dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved)));
        Assert.Equal(label, Assert.Single(ex.FailingChecks).Label);
        await Assert.ThrowsAsync<RequestValidationException>(async () =>
            await dev.Artifacts.AddCommentReviewAsync((await ArtifactsAsync(projectId, ArtifactType.DataEntity))[0], " "));

        await DeveloperReviewDesignAsync(projectId);
        Assert.True((await StageAsync(projectId, ProjectStatus.Design)).Passed);

        // The review is tied to the version: a new version of one design artifact needs a new review.
        var entity = (await ArtifactsAsync(projectId, ArtifactType.DataEntity))[0];
        await _host.ExecuteAsync($"UPDATE Artifacts SET CurrentVersion = CurrentVersion + 1 WHERE Id = '{entity}'");
        Assert.False(Check(await StageAsync(projectId, ProjectStatus.Design), label));

        // Developer reviews are comment-only rows recorded under the Developer role; they approve nothing.
        Assert.Equal(0, await _host.ScalarAsync(
            $"SELECT COUNT(*) FROM ArtifactReviews WHERE ReviewerRole = 'Developer' AND Decision <> {(int)ReviewDecision.CommentOnly}"));
    }

    [Fact]
    public async Task Design_exit_fails_when_an_approved_requirement_is_reopened_or_a_design_has_an_open_impact_notice()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        Assert.True((await StageAsync(projectId, ProjectStatus.Design)).Passed);

        var fr = (await ArtifactsAsync(projectId, ArtifactType.FunctionalRequirement))[0];
        var design = (await ArtifactsAsync(projectId, ArtifactType.DesignArtifact))[0];
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 2 WHERE Id = '{fr}'"); // UnderReview
        await _host.ExecuteAsync($"""
            INSERT INTO ArtifactImpactNotices (Id, ProjectId, SourceArtifactId, SourceVersion, AffectedArtifactId, Path, CreatedByUserId, CreatedAt)
            VALUES (NEWID(), '{projectId}', '{fr}', 2, '{design}', 'changed', '{_ba.Id}', GETUTCDATE())
            """);

        var stage = await StageAsync(projectId, ProjectStatus.Design);
        Assert.False(Check(stage, "Requirements baseline still approved"));
        Assert.False(Check(stage, "No open impact notices on design"));
    }

    // ---- rejection / withdrawal ------------------------------------------------------------

    [Fact]
    public async Task Rejection_needs_a_reason_keeps_the_stage_and_is_kept_in_history()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        await SetStatusAsync(projectId, ProjectStatus.Design);

        using var dev = _host.As(_dev);
        using var reviewer = _host.As(_reviewer);
        var pending = await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(false)));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(false, "  ")));

        var rejected = await reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(false, "API error handling is not specified."));
        Assert.Equal(StageTransitionState.Rejected, rejected.State);
        Assert.Equal("API error handling is not specified.", rejected.DecisionReason);
        var review = Assert.Single(rejected.Reviews);
        Assert.Equal(ReviewDecision.Rejected, review.Decision);
        Assert.Equal("API error handling is not specified.", review.Comment);
        Assert.Equal(ProjectStatus.Design, await StatusAsync(projectId));

        // Fixed and resubmitted: a new request, the rejected one stays in history.
        var again = await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved, "Error handling added."));
        Assert.Equal(StageTransitionState.Pending, again.State);
        var history = await dev.Lifecycle.GetHistoryAsync(projectId);
        Assert.Equal([StageTransitionState.Completed, StageTransitionState.Rejected, StageTransitionState.Pending], history.Select(h => h.State));
    }

    [Fact]
    public async Task The_requester_can_withdraw_a_pending_request_and_others_cannot()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        await SetStatusAsync(projectId, ProjectStatus.Design);

        using var dev = _host.As(_dev);
        var pending = await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved));

        using (var reviewer = _host.As(_reviewer))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reviewer.Lifecycle.WithdrawAsync(pending.Id, null));
        }

        var withdrawn = await dev.Lifecycle.WithdrawAsync(pending.Id, "Found a gap myself.");
        Assert.Equal(StageTransitionState.Withdrawn, withdrawn.State);
        Assert.Equal(ProjectStatus.Design, await StatusAsync(projectId));
        await Assert.ThrowsAsync<ConflictException>(() => dev.Lifecycle.WithdrawAsync(pending.Id, null));

        // Nothing is pending any more, so a new submission is accepted.
        Assert.Equal(StageTransitionState.Pending,
            (await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved))).State);
    }

    [Fact]
    public async Task Evidence_is_judged_again_at_approval_and_only_an_administrator_can_override_then()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        await SetStatusAsync(projectId, ProjectStatus.Design);

        using var dev = _host.As(_dev);
        var pending = await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved));

        // A design artifact is reopened after submission.
        var design = (await ArtifactsAsync(projectId, ArtifactType.DesignArtifact))[0];
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 2 WHERE Id = '{design}'");

        using (var reviewer = _host.As(_reviewer))
        {
            var ex = await Assert.ThrowsAsync<LifecycleEvidenceException>(() =>
                reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true)));
            Assert.Contains(ex.FailingChecks, c => c.Label == "Solution design approved");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true, OverrideReason: "fine")));
        }

        Assert.Equal(ProjectStatus.Design, await StatusAsync(projectId));

        using var admin = _host.As(_admin);
        var approved = await admin.Lifecycle.DecideAsync(pending.Id,
            new DecideStageTransitionRequest(true, OverrideReason: "Wording-only change; re-review tracked separately."));
        Assert.True(approved.IsOverride);
        Assert.Equal(Roles.Administrator, approved.DecidedByRole);
        Assert.Contains(approved.Evidence, c => c.Label == "Solution design approved" && !c.Passed);
        Assert.Equal(ProjectStatus.Approved, await StatusAsync(projectId));
    }

    // ---- Approved -> Implementation -> Testing -> Completed ---------------------------------

    [Fact]
    public async Task Late_stages_advance_on_work_records_and_the_testing_gate_needs_qa_sign_off()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        await SetStatusAsync(projectId, ProjectStatus.Approved);
        var tasks = await ArtifactsAsync(projectId, ArtifactType.ImplementationTask);
        var tests = await ArtifactsAsync(projectId, ArtifactType.TestCase);

        using var reviewer = _host.As(_reviewer);
        using var dev = _host.As(_dev);
        using var qa = _host.As(_qa);

        // Approved (owner: Reviewer) -> Implementation, direct.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Implementation)));
        await reviewer.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Implementation));
        Assert.Equal(ProjectStatus.Implementation, await StatusAsync(projectId));

        // Approved tasks are not implementation evidence; a Developer's completion record is.
        var state = await dev.Lifecycle.GetStateAsync(projectId);
        Assert.False(state.ReadyToAdvance);
        Assert.Contains(state.ExitChecks, c => c.Label == "Implementation tasks completed" && !c.Passed);
        await Assert.ThrowsAsync<LifecycleEvidenceException>(() =>
            dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Testing)));

        foreach (var task in tasks)
        {
            await dev.Work.RecordAsync(task, new(WorkOutcome.TaskCompleted, "Merged."));
        }

        Assert.True((await dev.Lifecycle.GetStateAsync(projectId)).ReadyToAdvance);
        await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Testing));
        Assert.Equal(ProjectStatus.Testing, await StatusAsync(projectId));

        // Testing: QA outcomes are the verification evidence.
        Assert.False(Check(await StageAsync(projectId, ProjectStatus.Testing), "Test cases passed"));
        foreach (var test in tests)
        {
            await qa.Work.RecordAsync(test, new(WorkOutcome.TestPassed, null));
        }

        var testing = await qa.Lifecycle.GetStateAsync(projectId);
        Assert.True(testing.ReadyToAdvance);
        Assert.True(testing.RequiresSignOff);
        Assert.True(testing.Actions.CanSubmitForApproval);

        // No sign-off note -> the sign-off check fails.
        var ex = await Assert.ThrowsAsync<LifecycleEvidenceException>(() =>
            qa.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Completed)));
        Assert.Equal(ProjectLifecycleService.SignOffLabel, Assert.Single(ex.FailingChecks).Label);

        var pending = await qa.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Completed, "All tests passed on staging."));
        Assert.Equal(StageTransitionState.Pending, pending.State);
        Assert.Equal(ProjectStatus.Testing, await StatusAsync(projectId));

        var done = await reviewer.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true));
        Assert.Contains(done.Evidence, c => c.Label == ProjectLifecycleService.SignOffLabel && c.Passed);
        Assert.Equal(ProjectStatus.Completed, await StatusAsync(projectId));

        var completed = await reviewer.Lifecycle.GetStateAsync(projectId);
        Assert.Empty(completed.ExitChecks);
        Assert.Null(completed.NextStatus);
        Assert.False(completed.Actions.CanAdvance || completed.Actions.CanSubmitForApproval);
    }

    // ---- evidence clarifications -----------------------------------------------------------

    [Fact]
    public async Task Analysis_does_not_require_business_rules_or_nfrs_that_do_not_exist_but_existing_ones_must_be_approved()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        var removed = string.Join(",", (await ArtifactsAsync(projectId, ArtifactType.NonFunctionalRequirement, ArtifactType.BusinessRule)).Select(id => $"'{id}'"));
        if (removed.Length > 0)
        {
            await _host.ExecuteAsync($"DELETE FROM ArtifactRelationships WHERE SourceArtifactId IN ({removed}) OR TargetArtifactId IN ({removed})");
            await _host.ExecuteAsync($"DELETE FROM Artifacts WHERE Id IN ({removed})");
        }

        var analysis = await StageAsync(projectId, ProjectStatus.Analysis);
        Assert.True(Check(analysis, "Non-functional requirements approved"));
        Assert.True(Check(analysis, "Business rules approved"));

        // One that exists but isn't approved still blocks.
        await _host.ExecuteAsync($"""
            INSERT INTO Artifacts (Id, ProjectId, ArtifactType, Code, Title, Status, Origin, DataJson, CurrentVersion, CreatedByUserId, CreatedAt, UpdatedAt)
            VALUES (NEWID(), '{projectId}', {(int)ArtifactType.BusinessRule}, 'BR-900', 'Leave over 10 days needs second approval', 0, 1, '{"{}"}', 1, '{_ba.Id}', GETUTCDATE(), GETUTCDATE())
            """);
        Assert.False(Check(await StageAsync(projectId, ProjectStatus.Analysis), "Business rules approved"));
    }

    [Fact]
    public async Task Testing_requires_tests_and_verification_only_for_approved_functional_requirements()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        using (var dev = _host.As(_dev))
        {
            foreach (var task in await ArtifactsAsync(projectId, ArtifactType.ImplementationTask))
                await dev.Work.RecordAsync(task, new(WorkOutcome.TaskCompleted, null));
        }

        using (var qa = _host.As(_qa))
        {
            foreach (var test in await ArtifactsAsync(projectId, ArtifactType.TestCase))
                await qa.Work.RecordAsync(test, new(WorkOutcome.TestPassed, null));
        }

        Assert.True((await StageAsync(projectId, ProjectStatus.Testing)).Passed);

        // A not-yet-approved FR and a rejected FR with no tests at all don't block Testing.
        var draftFr = Guid.NewGuid();
        await _host.ExecuteAsync($"""
            INSERT INTO Artifacts (Id, ProjectId, ArtifactType, Code, Title, Status, Origin, DataJson, CurrentVersion, CreatedByUserId, CreatedAt, UpdatedAt)
            VALUES ('{draftFr}', '{projectId}', 0, 'FR-900', 'Export leave to payroll', 2, 1, '{"{}"}', 1, '{_ba.Id}', GETUTCDATE(), GETUTCDATE()),
                   (NEWID(), '{projectId}', 0, 'FR-901', 'Rejected idea', 4, 1, '{"{}"}', 1, '{_ba.Id}', GETUTCDATE(), GETUTCDATE())
            """);
        var testing = await StageAsync(projectId, ProjectStatus.Testing);
        Assert.True(Check(testing, "Every approved functional requirement has an approved test case"));
        Assert.True(Check(testing, "Functional requirements verified"));
        Assert.True(Check(testing, "Every approved functional requirement fully traced"));
        Assert.True(testing.Passed);

        // Once approved, it needs an approved test and verification like the others.
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 3 WHERE Id = '{draftFr}'");
        testing = await StageAsync(projectId, ProjectStatus.Testing);
        Assert.False(Check(testing, "Every approved functional requirement has an approved test case"));
        Assert.False(Check(testing, "Functional requirements verified"));
    }

    // ---- concurrency -----------------------------------------------------------------------

    [Fact]
    public async Task Two_simultaneous_approvals_cannot_both_succeed()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId);
        await SetStatusAsync(projectId, ProjectStatus.Design);
        StageTransitionResponse pending;
        using (var dev = _host.As(_dev))
        {
            pending = await dev.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.Approved));
        }

        using var a = _host.As(_reviewer);
        using var b = _host.As(_admin);
        var results = await Task.WhenAll(
            TryAsync(() => a.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(true))),
            TryAsync(() => b.Lifecycle.DecideAsync(pending.Id, new DecideStageTransitionRequest(false, "No."))));

        Assert.Single(results, r => r is null);
        Assert.IsAssignableFrom<ConflictException>(Assert.Single(results, r => r is not null));
        Assert.Equal(1, await _host.ScalarAsync($"SELECT COUNT(*) FROM ProjectStageReviews WHERE TransitionId = '{pending.Id}'"));
    }

    [Fact]
    public async Task Two_simultaneous_advances_from_the_same_stage_record_only_one()
    {
        var projectId = await ProjectAsync();
        using var a = _host.As(_ba);
        using var b = _host.As(_admin);

        var results = await Task.WhenAll(
            TryAsync(() => a.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering))),
            TryAsync(() => b.Lifecycle.AdvanceAsync(projectId, new AdvanceStageRequest(ProjectStatus.RequirementsGathering))));

        Assert.Single(results, r => r is null);
        Assert.IsAssignableFrom<ConflictException>(Assert.Single(results, r => r is not null));
        Assert.Equal(ProjectStatus.RequirementsGathering, await StatusAsync(projectId));
        Assert.Equal(2, await TransitionCountAsync(projectId));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_pending_request()
    {
        var projectId = await ProjectAsync();
        string Pending(int sequence) => $"""
            INSERT INTO ProjectStageTransitions (Id, ProjectId, Sequence, Kind, State, FromStatus, ToStatus, RequestedByUserId, RequestedAt, IsOverride, EvidenceSnapshotJson)
            VALUES (NEWID(), '{projectId}', {sequence}, 1, 0, 3, 4, 'u', GETUTCDATE(), 0, '[]')
            """;

        await _host.ExecuteAsync(Pending(1));
        await Assert.ThrowsAsync<SqlException>(() => _host.ExecuteAsync(Pending(2)));
    }

    // ---- existing projects / agents --------------------------------------------------------

    [Fact]
    public async Task A_project_past_stages_it_has_no_evidence_for_keeps_its_stage_and_reports_the_gaps()
    {
        var projectId = await ProjectAsync();
        await SetStatusAsync(projectId, ProjectStatus.Implementation);

        using var s = _host.As(_dev);
        var state = await s.Lifecycle.GetStateAsync(projectId);

        Assert.Equal(ProjectStatus.Implementation, state.Status);
        Assert.Equal(ProjectStatus.Implementation, await StatusAsync(projectId));
        Assert.Equal(
            [ProjectStatus.RequirementsGathering, ProjectStatus.Analysis, ProjectStatus.Design, ProjectStatus.Approved],
            state.EvidenceGaps.Select(g => g.Stage));
        Assert.DoesNotContain(state.EvidenceGaps, g => g.Stage == ProjectStatus.Draft); // Draft's checks do hold
    }

    [Fact]
    public async Task A_completed_agent_run_neither_moves_the_project_nor_counts_as_evidence()
    {
        var projectId = await ProjectAsync();
        var (_, source) = await _ba.Api.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        var (rs, run) = await _ba.Api.PostAsync("/api/agent-runs", new { requirementSourceId = Id(source) });
        Assert.Equal(200, rs);

        for (var step = 0; step < 6; step++)
        {
            var (ds, decided) = await _admin.Api.PostAsync($"/api/agent-runs/{Id(run)}/decision", new { approve = true });
            Assert.Equal(200, ds);
            run = decided;
        }

        Assert.Equal((int)AgentRunStatus.Completed, run!["status"]!.GetValue<int>());
        Assert.Equal(ProjectStatus.Draft, await StatusAsync(projectId));
        Assert.Equal(1, await TransitionCountAsync(projectId));

        // The agents produced FRs, stories, design, tasks and tests - all still AI proposals.
        Assert.NotEmpty(await ArtifactsAsync(projectId, ArtifactType.FunctionalRequirement));
        var analysis = await StageAsync(projectId, ProjectStatus.Analysis);
        Assert.False(Check(analysis, "Functional requirements approved"));
        Assert.False((await StageAsync(projectId, ProjectStatus.Design)).Passed);
        Assert.False((await StageAsync(projectId, ProjectStatus.Implementation)).Passed);
        Assert.False((await StageAsync(projectId, ProjectStatus.Testing)).Passed);
    }
}
