using System.Reflection;
using System.Text.Json.Nodes;
using AiReap.Application.Artifacts;
using AiReap.Application.Lifecycle;
using AiReap.Application.Sdlc;
using AiReap.Domain.Common;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// "How to meet this" guidance on lifecycle exit checks (ExitCheckCatalog / ExitCheckGuidanceService):
// every check is covered, pass/fail is untouched, who-can-fix follows the real permission and
// evidence rules per role, Developer visibility holds, and guidance never reaches stored history.
public class ExitCheckGuidanceTests : IAsyncLifetime
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

    private async Task<Guid> ProjectAsync(bool withScope = true, params Actor[] members)
    {
        var (status, body) = await _ba.Api.PostAsync("/api/projects", new
        {
            name = "Room booking",
            description = "d",
            businessProblem = "Rooms are double-booked.",
            objectives = "No double bookings",
            scope = withScope ? "Staff and admins" : null
        });
        Assert.Equal(201, status);
        var projectId = Id(body);
        foreach (var member in members.Length > 0 ? members : [_dev, _reviewer, _qa])
        {
            Assert.InRange((await _host.AddMemberAsync(_ba.Api, projectId, member.Api)).Status, 200, 201);
        }

        return Guid.Parse(projectId);
    }

    private Task SetStatusAsync(Guid projectId, ProjectStatus status) =>
        _host.ExecuteAsync($"UPDATE Projects SET Status = {(int)status} WHERE Id = '{projectId}'");

    private async Task<List<(Guid Id, string Code)>> ArtifactsAsync(Guid projectId, ArtifactType type)
    {
        await using var conn = new SqlConnection(_host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT Id, Code FROM Artifacts WHERE ProjectId = '{projectId}' AND ArtifactType = {(int)type} ORDER BY Code", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.GetString(1)));
        return rows;
    }

    // The full generated chain (FR -> story -> AC, design, tasks, tests) via the stub AI, left in its
    // generated (unapproved) state unless `approve`.
    private async Task FullChainAsync(Guid projectId, bool approve)
    {
        var (_, source) = await _ba.Api.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Staff book rooms by slot; admins manage rooms." });
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

        if (approve)
        {
            await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 3, ApprovedVersion = CurrentVersion WHERE ProjectId = '{projectId}' AND Status <> 4");
        }
    }

    private async Task<ProjectLifecycleState> StateAsync(Actor actor, Guid projectId)
    {
        using var s = _host.As(actor);
        return await s.Lifecycle.GetStateAsync(projectId);
    }

    private static ExitCheckView CheckByKey(ProjectLifecycleState state, string key) =>
        state.ExitChecks.Single(c => c.Guidance?.Key == key);

    private static BlockerGroup GroupOf(ExitCheckView check, string reason) =>
        check.Guidance!.Blockers.Single(b => b.Reason == reason);

    // ---- coverage --------------------------------------------------------------------------

    [Fact]
    public async Task Every_exit_check_of_every_stage_has_a_catalog_entry_and_every_failing_check_has_guidance()
    {
        var empty = await ProjectAsync(withScope: false);
        var generated = await ProjectAsync();
        await FullChainAsync(generated, approve: false);
        var approved = await ProjectAsync();
        await FullChainAsync(approved, approve: true);

        var keysSeen = new HashSet<string>();
        using var s = _host.As(_admin);
        foreach (var projectId in new[] { empty, generated, approved })
        {
            var stages = await s.Evidence.GetDetailedAsync(projectId);
            Assert.Equal(8, stages.Count);
            var view = await s.Guidance.ForProjectAsync(projectId);
            foreach (var check in stages.SelectMany(st => st.Checks))
            {
                keysSeen.Add(check.Key);
                Assert.True(ExitCheckCatalog.TryGet(check.Key, out var guide), $"No catalog entry for {check.Key}");
                var rendered = view(check);
                if (check.Check.Passed)
                {
                    Assert.Empty(check.Blockers);
                    Assert.Null(rendered.Guidance);
                    continue;
                }

                Assert.NotEmpty(check.Blockers);
                foreach (var blocker in check.Blockers)
                {
                    Assert.True(guide.Reasons.ContainsKey(blocker.Reason), $"{check.Key} has no guidance for reason {blocker.Reason}");
                }

                Assert.NotNull(rendered.Guidance);
                Assert.Equal(check.Blockers.Count, rendered.Guidance!.Blockers.Count);
                Assert.All(rendered.Guidance.Blockers, g => Assert.False(string.IsNullOrWhiteSpace(g.Instruction)));
            }
        }

        // Every check the evidence service emits, plus the sign-off ProjectLifecycleService adds, is catalogued - and nothing else is.
        keysSeen.Add(ExitCheckKeys.QaSignOff);
        Assert.Equal(ExitCheckCatalog.Checks.Keys.OrderBy(k => k), keysSeen.OrderBy(k => k));
    }

    [Fact]
    public async Task Guidance_does_not_change_pass_fail_or_detail()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId, approve: true);
        await SetStatusAsync(projectId, ProjectStatus.Implementation);

        foreach (var actor in new[] { _admin, _ba, _dev, _qa, _reviewer })
        {
            using var s = _host.As(actor);
            var plain = await s.Evidence.GetAsync(projectId);
            var detailed = await s.Evidence.GetDetailedAsync(projectId);
            Assert.Equal(plain.SelectMany(st => st.Checks), detailed.SelectMany(st => st.Checks.Select(c => c.Check)));

            var state = await s.Lifecycle.GetStateAsync(projectId);
            var current = plain.Single(st => st.Stage == ProjectStatus.Implementation);
            Assert.Equal(current.Checks.Select(c => (c.Label, c.Passed, c.Detail)), state.ExitChecks.Select(c => (c.Label, c.Passed, c.Detail)));
            Assert.Equal(current.Passed, state.ReadyToAdvance);
        }
    }

    [Fact]
    public void Unknown_keys_and_the_item_cap_degrade_gracefully()
    {
        using var s = _host.As(_admin);
        var projectId = Guid.NewGuid();
        var unknown = new CheckEvidence(new SdlcCheck("Something new", false, "Not met."), "future.unknown-check", [CheckBlocker.Of("whatever")]);
        var many = Enumerable.Range(1, 250).Select(i => new BlockerArtifact(new Artifact
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Code = $"TASK-{i:000}", Title = "t", ArtifactType = ArtifactType.ImplementationTask, Status = ArtifactStatus.Approved
        })).ToList();
        var capped = new CheckEvidence(new SdlcCheck("Implementation tasks completed", false, "250 of 250"), ExitCheckKeys.TasksCompleted,
            [new CheckBlocker(BlockerReasons.NotCompleted, many)]);

        var views = s.Guidance.ViewsAsync(projectId, [unknown, capped]).GetAwaiter().GetResult();

        Assert.Equal(("Something new", false, "Not met."), (views[0].Label, views[0].Passed, views[0].Detail));
        Assert.Null(views[0].Guidance);
        var group = views[1].Guidance!.Blockers.Single();
        Assert.Equal(ExitCheckGuidanceService.MaxItems, group.Items.Count);
        Assert.Equal(250, group.TotalCount);
        Assert.Equal(0, group.HiddenCount);
    }

    // ---- Implementation: blocker groups and who can fix them, per role -------------------------

    [Fact]
    public async Task Implementation_tasks_split_into_approval_completion_and_reopened_groups_with_role_specific_fixers()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId, approve: true);
        await SetStatusAsync(projectId, ProjectStatus.Implementation);
        // Three more approved tasks (copies of the generated one), so every group has a member.
        var generated = await ArtifactsAsync(projectId, ArtifactType.ImplementationTask);
        await _host.ExecuteAsync(
            "INSERT INTO Artifacts (Id, ProjectId, ArtifactType, Code, Title, Priority, Status, Origin, RequirementSourceId, DataJson, CurrentVersion, ApprovedVersion, CreatedByUserId, CreatedAt, UpdatedByUserId, UpdatedAt) " +
            "SELECT NEWID(), ProjectId, ArtifactType, CONCAT('TASK-9', v.n), Title, Priority, Status, Origin, RequirementSourceId, DataJson, CurrentVersion, ApprovedVersion, CreatedByUserId, CreatedAt, UpdatedByUserId, UpdatedAt " +
            $"FROM Artifacts CROSS JOIN (VALUES (1), (2), (3)) v(n) WHERE Id = '{generated[0].Id}'");
        var tasks = await ArtifactsAsync(projectId, ArtifactType.ImplementationTask);
        Assert.True(tasks.Count >= 4, $"Expected at least four tasks, got {tasks.Count}.");

        // tasks[0]: back under review (needs approval). tasks[1]: completed. tasks[2]: completed then reopened. Rest: not completed.
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 2 WHERE Id = '{tasks[0].Id}'");
        using (var dev = _host.As(_dev))
        {
            await dev.Work.RecordAsync(tasks[1].Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null));
            await dev.Work.RecordAsync(tasks[2].Id, new RecordWorkRequest(WorkOutcome.TaskCompleted, null));
        }

        using (var dev = _host.As(_dev))
        {
            await dev.Work.RecordAsync(tasks[2].Id, new RecordWorkRequest(WorkOutcome.TaskReopened, "Bug found"));
        }

        var devState = await StateAsync(_dev, projectId);
        var check = CheckByKey(devState, ExitCheckKeys.TasksCompleted);
        Assert.Equal(CheckKinds.Action, check.Guidance!.Kind);
        Assert.Equal(
            new[] { BlockerReasons.NeedsApproval, BlockerReasons.NotCompleted, BlockerReasons.TaskReopened },
            check.Guidance.Blockers.Select(b => b.Reason));

        var approval = GroupOf(check, BlockerReasons.NeedsApproval);
        Assert.Equal(CheckKinds.Approval, approval.Kind);
        Assert.Equal(new[] { tasks[0].Code }, approval.Items.Select(i => i.Code));
        var notCompleted = GroupOf(check, BlockerReasons.NotCompleted);
        Assert.Equal(tasks.Skip(3).Select(t => t.Code), notCompleted.Items.Select(i => i.Code));
        Assert.DoesNotContain(check.Guidance.Blockers.SelectMany(b => b.Items), i => i.Code == tasks[1].Code);
        Assert.Equal(new[] { tasks[2].Code }, GroupOf(check, BlockerReasons.TaskReopened).Items.Select(i => i.Code));
        Assert.All(check.Guidance.Blockers.SelectMany(b => b.Items), i => Assert.NotNull(i.RequirementSourceId));

        // Who can fix what, as each role sees it.
        Assert.False(approval.CallerCanFix);
        Assert.True(notCompleted.CallerCanFix);

        var admin = CheckByKey(await StateAsync(_admin, projectId), ExitCheckKeys.TasksCompleted);
        Assert.True(GroupOf(admin, BlockerReasons.NeedsApproval).CallerCanFix);
        // An Administrator's work record is not Developer evidence: not a fix, and told why.
        Assert.False(GroupOf(admin, BlockerReasons.NotCompleted).CallerCanFix);
        Assert.Contains("override", GroupOf(admin, BlockerReasons.NotCompleted).CallerNote);

        foreach (var (actor, canApprove) in new[] { (_ba, true), (_reviewer, true), (_qa, false) })
        {
            var view = CheckByKey(await StateAsync(actor, projectId), ExitCheckKeys.TasksCompleted);
            Assert.Equal(canApprove, GroupOf(view, BlockerReasons.NeedsApproval).CallerCanFix);
            Assert.False(GroupOf(view, BlockerReasons.NotCompleted).CallerCanFix);
            Assert.Null(GroupOf(view, BlockerReasons.NotCompleted).CallerNote);
        }

        // Assignees are the project members holding a fix role (the Administrator is not a member).
        Assert.Equal(new[] { _dev.Id }, notCompleted.Assignees);
        Assert.Equal(new[] { _ba.Id, _reviewer.Id }.OrderBy(i => i, StringComparer.Ordinal), approval.Assignees);

        // "Functional requirements implemented" is automatic and follows from the task checks.
        var implemented = CheckByKey(devState, ExitCheckKeys.FrsImplemented);
        Assert.Equal(CheckKinds.Automatic, implemented.Guidance!.Kind);
        Assert.Contains(ExitCheckKeys.TasksCompleted, implemented.Guidance.DependsOn);
        var waiting = GroupOf(implemented, BlockerReasons.WaitingOnTasks);
        Assert.All(waiting.Items, i => Assert.StartsWith("waiting on ", i.Note));
        Assert.True(waiting.CallerCanFix);
    }

    [Fact]
    public async Task No_member_holding_the_fix_role_leaves_assignees_empty()
    {
        var projectId = await ProjectAsync(withScope: true, _reviewer, _qa);
        await FullChainAsync(projectId, approve: true);
        await SetStatusAsync(projectId, ProjectStatus.Implementation);

        var check = CheckByKey(await StateAsync(_ba, projectId), ExitCheckKeys.TasksCompleted);
        var notCompleted = GroupOf(check, BlockerReasons.NotCompleted);
        Assert.Empty(notCompleted.Assignees);
        Assert.Equal(new[] { Roles.Developer }, notCompleted.FixRoles);
    }

    // ---- empty states, approvals, sign-off -------------------------------------------------

    [Fact]
    public async Task Empty_analysis_asks_writers_to_generate_before_anything_can_be_approved()
    {
        var projectId = await ProjectAsync();
        await SetStatusAsync(projectId, ProjectStatus.Analysis);

        var state = await StateAsync(_reviewer, projectId);
        var frs = CheckByKey(state, ExitCheckKeys.FrsApproved);
        var none = GroupOf(frs, BlockerReasons.NoneYet);
        Assert.Equal(CheckKinds.Action, none.Kind);
        Assert.Equal(new[] { Roles.BusinessAnalyst, Roles.Administrator }, none.FixRoles);
        Assert.False(none.CallerCanFix);
        Assert.Equal(new[] { _ba.Id }, none.Assignees);
        Assert.Equal(CheckAreas.Analysis, none.Destination.Area);

        // A Reviewer can run quality analysis and conflict detection.
        Assert.True(GroupOf(CheckByKey(state, ExitCheckKeys.QualityRun), BlockerReasons.NotRun).CallerCanFix);
        Assert.True(GroupOf(CheckByKey(state, ExitCheckKeys.ConflictRun), BlockerReasons.NotRun).CallerCanFix);
    }

    [Fact]
    public async Task Testing_submission_without_sign_off_returns_409_with_guidance_and_history_stores_no_guidance()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId, approve: true);
        await SetStatusAsync(projectId, ProjectStatus.Testing);

        var (status, body) = await _qa.Api.PostAsync($"/api/projects/{projectId}/lifecycle/transitions", new { targetStatus = (int)ProjectStatus.Completed });
        Assert.Equal(409, status);
        var failing = body!["failingChecks"]!.AsArray();
        Assert.All(failing, c => Assert.NotNull(c!["guidance"]));

        var signOff = failing.Single(c => c!["guidance"]!["key"]!.GetValue<string>() == ExitCheckKeys.QaSignOff)!;
        Assert.Equal(CheckKinds.SignOff, signOff["guidance"]!["kind"]!.GetValue<string>());
        var group = signOff["guidance"]!["blockers"]!.AsArray().Single()!;
        Assert.Equal(new[] { Roles.QA }, group["fixRoles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.True(group["callerCanFix"]!.GetValue<bool>());

        var passed = failing.Single(c => c!["guidance"]!["key"]!.GetValue<string>() == ExitCheckKeys.TestsPassed)!;
        var noResult = passed["guidance"]!["blockers"]!.AsArray().Single(b => b!["reason"]!.GetValue<string>() == BlockerReasons.NoResult)!;
        Assert.True(noResult["callerCanFix"]!.GetValue<bool>());
        Assert.NotEmpty(noResult["items"]!.AsArray());

        // An Administrator override is stored with the plain checks only: no per-viewer guidance in history.
        var (os, _) = await _admin.Api.PostAsync($"/api/projects/{projectId}/lifecycle/transitions",
            new { targetStatus = (int)ProjectStatus.Completed, overrideReason = "Demo" });
        Assert.InRange(os, 200, 201);
        await using var conn = new SqlConnection(_host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT EvidenceSnapshotJson FROM ProjectStageTransitions WHERE ProjectId = '{projectId}' AND Kind = {(int)StageTransitionKind.Advance}", conn);
        var snapshot = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("guidance", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ProjectLifecycleService.SignOffLabel, snapshot);

        var (_, history) = await _qa.Api.GetAsync($"/api/projects/{projectId}/lifecycle/history");
        Assert.All(history!.AsArray().SelectMany(t => t!["evidence"]!.AsArray()), c => Assert.Null(c!["guidance"]));
    }

    [Fact]
    public async Task Evidence_gaps_of_earlier_stages_carry_guidance()
    {
        var projectId = await ProjectAsync();
        await SetStatusAsync(projectId, ProjectStatus.Design);

        var state = await StateAsync(_ba, projectId);
        var analysis = state.EvidenceGaps.Single(g => g.Stage == ProjectStatus.Analysis);
        Assert.All(analysis.Checks.Where(c => !c.Passed), c => Assert.NotNull(c.Guidance));
        Assert.All(analysis.Checks.Where(c => c.Passed), c => Assert.Null(c.Guidance));
    }

    // ---- Developer visibility --------------------------------------------------------------

    [Fact]
    public async Task Developer_guidance_counts_hidden_requirements_but_never_identifies_them()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId, approve: false);
        await SetStatusAsync(projectId, ProjectStatus.Analysis);
        var frs = await ArtifactsAsync(projectId, ArtifactType.FunctionalRequirement);
        Assert.NotEmpty(frs);

        var check = CheckByKey(await StateAsync(_dev, projectId), ExitCheckKeys.FrsApproved);
        var group = GroupOf(check, BlockerReasons.NeedsApproval);
        Assert.Empty(group.Items);
        Assert.Equal(frs.Count, group.HiddenCount);
        Assert.Equal(frs.Count, group.TotalCount);
        Assert.False(group.CallerCanFix);

        // The BA sees them.
        var ba = GroupOf(CheckByKey(await StateAsync(_ba, projectId), ExitCheckKeys.FrsApproved), BlockerReasons.NeedsApproval);
        Assert.Equal(frs.Select(f => f.Code), ba.Items.Select(i => i.Code));
        Assert.Equal(0, ba.HiddenCount);

        // Over HTTP, nothing that identifies an unapproved requirement reaches the Developer.
        var (status, body) = await _dev.Api.GetAsync($"/api/projects/{projectId}/lifecycle");
        Assert.Equal(200, status);
        var json = body!.ToJsonString();
        foreach (var (id, code) in frs)
        {
            Assert.DoesNotContain(id.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"\"{code}\"", json);
        }
    }

    [Fact]
    public async Task A_conflict_with_a_hidden_requirement_is_described_without_naming_it()
    {
        var projectId = await ProjectAsync();
        await FullChainAsync(projectId, approve: true);
        var frs = await ArtifactsAsync(projectId, ArtifactType.FunctionalRequirement);
        var tasks = await ArtifactsAsync(projectId, ArtifactType.ImplementationTask);
        // A visible task (technical types are visible to a Developer in every status) conflicting with an unapproved FR.
        await _host.ExecuteAsync($"UPDATE Artifacts SET Status = 2 WHERE Id = '{frs[0].Id}'");
        await _host.ExecuteAsync(
            $"INSERT INTO ArtifactRelationships (Id, SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt) " +
            $"VALUES (NEWID(), '{tasks[0].Id}', '{frs[0].Id}', {(int)RelationshipType.ConflictsWith}, SYSUTCDATETIME())");
        await SetStatusAsync(projectId, ProjectStatus.Testing);

        var devGroup = GroupOf(CheckByKey(await StateAsync(_dev, projectId), ExitCheckKeys.ConflictsResolved), BlockerReasons.Unresolved);
        Assert.Equal(new[] { tasks[0].Code }, devGroup.Items.Select(i => i.Code));
        Assert.Equal("conflicts with 1 item you can't view", devGroup.Items.Single().Note);
        Assert.Equal(1, devGroup.HiddenCount);

        var baGroup = GroupOf(CheckByKey(await StateAsync(_ba, projectId), ExitCheckKeys.ConflictsResolved), BlockerReasons.Unresolved);
        Assert.Contains(baGroup.Items, i => i.Code == tasks[0].Code && i.Note == $"conflicts with {frs[0].Code}");
    }

    // ---- permissions drift -----------------------------------------------------------------

    // FixRoles must never promise a fix the API refuses: each is a subset of the roles the named
    // endpoint's [Authorize] allows (the same rule GET /api/roles/matrix reports).
    [Fact]
    public void Fix_roles_never_exceed_what_the_endpoint_allows()
    {
        var api = typeof(Program).Assembly;
        var checkedEndpoints = 0;
        foreach (var (key, guide) in ExitCheckCatalog.Checks)
        {
            foreach (var (reason, reasonGuide) in guide.Reasons)
            {
                Assert.NotEmpty(reasonGuide.FixRoles);
                Assert.All(reasonGuide.FixRoles, r => Assert.Contains(r, Roles.All));
                if (reasonGuide.Endpoint is null) continue;

                var parts = reasonGuide.Endpoint.Split('.');
                var controller = api.GetTypes().SingleOrDefault(t => t.Name == parts[0]);
                Assert.True(controller is not null, $"{key}/{reason}: no controller {parts[0]}");
                var methods = controller!.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == parts[1]).ToList();
                Assert.True(methods.Count == 1, $"{key}/{reason}: expected one action {reasonGuide.Endpoint}, found {methods.Count}");

                var roles = methods[0].GetCustomAttribute<AuthorizeAttribute>()?.Roles ?? controller.GetCustomAttribute<AuthorizeAttribute>()?.Roles;
                var allowed = string.IsNullOrWhiteSpace(roles) ? Roles.All : roles.Split(',', StringSplitOptions.TrimEntries);
                Assert.True(reasonGuide.FixRoles.All(allowed.Contains),
                    $"{key}/{reason}: fix roles [{string.Join(", ", reasonGuide.FixRoles)}] exceed {reasonGuide.Endpoint} [{string.Join(", ", allowed)}]");
                checkedEndpoints++;
            }
        }

        Assert.True(checkedEndpoints > 40);
    }
}
