using System.Text.Json.Nodes;
using AiReap.Domain.Common;
using AiReap.Domain.Enums;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// §5/§36 lifecycle alignment, phase 4: the six agents work inside the lifecycle without being it.
public class AgentLifecycleTests : IAsyncLifetime
{
    // Pipeline order, with the only non-admin role allowed to decide each step (unchanged).
    private static readonly (string Agent, string Approver, ProjectStatus[] Stages)[] Pipeline =
    [
        ("Requirements Agent", Roles.BusinessAnalyst, [ProjectStatus.RequirementsGathering]),
        ("Analysis Agent", Roles.BusinessAnalyst, [ProjectStatus.Analysis]),
        ("Architecture Agent", Roles.Developer, [ProjectStatus.Design]),
        ("Development Planning Agent", Roles.Developer, [ProjectStatus.Approved]),
        ("QA Agent", Roles.QA, [ProjectStatus.Testing]),
        ("Review Agent", Roles.Reviewer, [ProjectStatus.Design, ProjectStatus.Testing]),
    ];

    private const int AwaitingApproval = 2, Approved = 3, StepRejected = 4; // AgentStageStatus

    private readonly TestHost _host = new();
    private readonly Dictionary<string, Actor> _by = new();
    private Actor _admin = null!;

    public async Task InitializeAsync()
    {
        foreach (var role in new[] { Roles.BusinessAnalyst, Roles.Developer, Roles.QA, Roles.Reviewer })
        {
            _by[role] = await _host.ActorAsync(role);
        }

        _admin = await _host.AdminAsync();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private Actor Ba => _by[Roles.BusinessAnalyst];

    private async Task<(string ProjectId, string SourceId)> ProjectAsync(ProjectStatus status = ProjectStatus.Draft)
    {
        var (_, project) = await Ba.Api.PostAsync("/api/projects", new
        {
            name = "Leave system", businessProblem = "Paper forms", objectives = "Cut leave admin time", scope = "Employees"
        });
        var projectId = Id(project);
        foreach (var member in _by.Values.Where(a => a != Ba))
        {
            await _host.AddMemberAsync(Ba.Api, projectId, member.Api);
        }

        var (_, source) = await Ba.Api.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        await _host.ExecuteAsync($"UPDATE Projects SET Status = {(int)status} WHERE Id = '{projectId}'");
        return (projectId, Id(source));
    }

    private async Task<JsonNode> StartAsync(string sourceId)
    {
        var (s, run) = await Ba.Api.PostAsync("/api/agent-runs", new { requirementSourceId = sourceId });
        Assert.Equal(200, s);
        return run!;
    }

    // Decides steps from `from` up to (not including) `to`, each by its own approver role.
    private async Task<JsonNode> ApproveStepsAsync(JsonNode run, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var (s, next) = await _by[Pipeline[i].Approver].Api.PostAsync($"/api/agent-runs/{Id(run)}/decision", new { approve = true });
            Assert.Equal(200, s);
            run = next!;
        }

        return run;
    }

    private Task<int> StatusAsync(string projectId) => _host.ScalarAsync($"SELECT Status FROM Projects WHERE Id = '{projectId}'");

    private Task<int> HistoryCountAsync(string projectId) =>
        _host.ScalarAsync($"SELECT COUNT(*) FROM ProjectStageTransitions WHERE ProjectId = '{projectId}'");

    private static int[] Stages(JsonNode? list) => list!.AsArray().Select(n => n!.GetValue<int>()).ToArray();

    [Fact]
    public async Task Every_agent_reports_its_lifecycle_stages_and_keeps_its_approver_roles()
    {
        var (s, definitions) = await Ba.Api.GetAsync("/api/agents");
        Assert.Equal(200, s);
        var list = definitions!.AsArray();
        Assert.Equal(Pipeline.Length, list.Count);

        for (var i = 0; i < Pipeline.Length; i++)
        {
            var d = list[i]!;
            Assert.Equal(Pipeline[i].Agent, d["displayName"]!.GetValue<string>());
            Assert.Equal(Pipeline[i].Stages.Select(x => (int)x).ToArray(), Stages(d["lifecycleStages"]));
            Assert.False(string.IsNullOrWhiteSpace(d["lifecycleContribution"]!.GetValue<string>()));
            Assert.Equal(new[] { Roles.Administrator, Pipeline[i].Approver },
                d["approverRoles"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        }
    }

    [Fact]
    public async Task A_run_records_the_project_stage_it_started_in()
    {
        var (draftProject, draftSource) = await ProjectAsync();
        var run = await StartAsync(draftSource);
        Assert.Equal((int)ProjectStatus.Draft, run["projectStatusAtStart"]!.GetValue<int>());
        Assert.Equal((int)ProjectStatus.Draft, await _host.ScalarAsync($"SELECT ProjectStatusAtStart FROM AgentRuns WHERE Id = '{Id(run)}'"));

        var (_, designSource) = await ProjectAsync(ProjectStatus.Design);
        var designRun = await StartAsync(designSource);
        Assert.Equal((int)ProjectStatus.Design, designRun["projectStatusAtStart"]!.GetValue<int>());

        // Each step reports the lifecycle stage(s) it serves.
        Assert.Equal(new[] { (int)ProjectStatus.RequirementsGathering }, Stages(run["stages"]![0]!["lifecycleStages"]));
        Assert.Equal((int)ProjectStatus.Draft, await StatusAsync(draftProject));
    }

    [Fact]
    public async Task Running_ahead_of_the_stage_is_recorded_but_moves_nothing()
    {
        var (projectId, sourceId) = await ProjectAsync(ProjectStatus.Analysis);
        var run = await StartAsync(sourceId);
        var history = await HistoryCountAsync(projectId);

        // Through the Architecture step, which serves Design - ahead of Analysis.
        run = await ApproveStepsAsync(run, 0, 3);
        Assert.Equal(AwaitingApproval, run["stages"]![3]!["status"]!.GetValue<int>());
        Assert.Equal((int)ProjectStatus.Analysis, await StatusAsync(projectId));
        Assert.Equal(history, await HistoryCountAsync(projectId));

        var (_, summary) = await Ba.Api.GetAsync($"/api/projects/{projectId}/agent-runs/summary");
        var steps = summary!["recentRuns"]![0]!["steps"]!.AsArray();
        Assert.False(steps[0]!["aheadOfProjectStage"]!.GetValue<bool>()); // Requirements Gathering
        Assert.False(steps[1]!["aheadOfProjectStage"]!.GetValue<bool>()); // Analysis
        Assert.True(steps[2]!["aheadOfProjectStage"]!.GetValue<bool>());  // Design
        Assert.True(steps[3]!["aheadOfProjectStage"]!.GetValue<bool>());  // Approved
    }

    [Fact]
    public async Task A_whole_pipeline_keeps_its_role_gates_and_leaves_the_lifecycle_untouched()
    {
        var (projectId, sourceId) = await ProjectAsync();
        var run = await StartAsync(sourceId);
        var history = await HistoryCountAsync(projectId);

        for (var i = 0; i < Pipeline.Length; i++)
        {
            // Every other non-admin role is still refused this step, with "step" wording.
            foreach (var (role, actor) in _by.Where(x => x.Key != Pipeline[i].Approver))
            {
                var (s, body) = await actor.Api.PostAsync($"/api/agent-runs/{Id(run)}/decision", new { approve = true });
                Assert.Equal(403, s);
                Assert.Contains($"{Pipeline[i].Agent} step can only be decided by", body!["message"]!.GetValue<string>());
            }

            run = await ApproveStepsAsync(run, i, i + 1);
        }

        Assert.Equal((int)AgentRunStatus.Completed, run["status"]!.GetValue<int>());
        Assert.Equal((int)ProjectStatus.Draft, await StatusAsync(projectId));
        Assert.Equal(history, await HistoryCountAsync(projectId));

        // The agents' output is still only AI proposals: no stage past Draft is evidenced by it.
        using var s2 = _host.As(_admin);
        var evidence = await s2.Evidence.GetAsync(Guid.Parse(projectId));
        foreach (var stage in evidence.Where(e => e.Stage is > ProjectStatus.RequirementsGathering and < ProjectStatus.Completed))
        {
            Assert.False(stage.Passed, $"{stage.Label} should not pass on agent output alone");
        }

        Assert.Contains(evidence.Single(e => e.Stage == ProjectStatus.Analysis).Checks,
            c => c.Label == "Functional requirements approved" && !c.Passed);
        var (_, lifecycle) = await Ba.Api.GetAsync($"/api/projects/{projectId}/lifecycle");
        Assert.Equal((int)ProjectStatus.Draft, lifecycle!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Review_agent_reports_the_lifecycle_evidence_rather_than_its_own_count()
    {
        var (projectId, sourceId) = await ProjectAsync(ProjectStatus.Analysis);
        var run = await ApproveStepsAsync(await StartAsync(sourceId), 0, 5);
        var review = run["stages"]![5]!;
        Assert.Equal(AwaitingApproval, review["status"]!.GetValue<int>());
        var output = review["output"]!;
        var metrics = output["metrics"]!;

        using var s = _host.As(_admin);
        var evidence = await s.Evidence.GetAsync(Guid.Parse(projectId));
        int Failing(ProjectStatus stage) => evidence.Single(e => e.Stage == stage).Checks.Count(c => !c.Passed);

        Assert.Equal(Failing(ProjectStatus.Analysis), metrics["currentStageChecksFailing"]!.GetValue<int>());
        Assert.Equal(Failing(ProjectStatus.Design), metrics["designGateChecksFailing"]!.GetValue<int>());
        Assert.Equal(Failing(ProjectStatus.Testing), metrics["testingGateChecksFailing"]!.GetValue<int>());
        Assert.Null(metrics["requirementsWithoutTests"]); // the old, separate calculation is gone

        // Each finding is a failing lifecycle check, named by the move it blocks.
        var findings = output["findings"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        var designFailing = evidence.Single(e => e.Stage == ProjectStatus.Design).Checks.Where(c => !c.Passed).ToList();
        Assert.NotEmpty(designFailing);
        Assert.All(designFailing, c => Assert.Contains($"Design → Approved: {c.Label} — {c.Detail}", findings));
        Assert.Equal(designFailing.Count + evidence.Single(e => e.Stage == ProjectStatus.Testing).Checks.Count(c => !c.Passed)
            + Failing(ProjectStatus.Analysis), findings.Count(f => !f.StartsWith("Evidence gap")));
        Assert.Contains("does not approve the project stage", output["reviewGuidance"]!.GetValue<string>());

        // Approving the Review step completes the run - and nothing else.
        run = await ApproveStepsAsync(run, 5, 6);
        Assert.Equal((int)AgentRunStatus.Completed, run["status"]!.GetValue<int>());
        Assert.Equal((int)ProjectStatus.Analysis, await StatusAsync(projectId));
    }

    [Fact]
    public async Task Summary_groups_steps_by_lifecycle_stage_and_shows_pending_and_rejected_decisions()
    {
        var (projectId, sourceId) = await ProjectAsync(ProjectStatus.RequirementsGathering);
        var run = await StartAsync(sourceId);

        var (s, summary) = await _by[Roles.QA].Api.GetAsync($"/api/projects/{projectId}/agent-runs/summary");
        Assert.Equal(200, s);
        Assert.Equal((int)ProjectStatus.RequirementsGathering, summary!["currentProjectStatus"]!.GetValue<int>());

        var byStage = summary["byLifecycleStage"]!.AsArray();
        Assert.Equal(8, byStage.Count);
        string[] AgentsIn(ProjectStatus stage) => byStage[(int)stage]!["agents"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Empty(AgentsIn(ProjectStatus.Draft));
        Assert.Equal(new[] { "Requirements Agent" }, AgentsIn(ProjectStatus.RequirementsGathering));
        Assert.Equal(new[] { "Analysis Agent" }, AgentsIn(ProjectStatus.Analysis));
        Assert.Equal(new[] { "Architecture Agent", "Review Agent" }, AgentsIn(ProjectStatus.Design));
        Assert.Equal(new[] { "Development Planning Agent" }, AgentsIn(ProjectStatus.Approved));
        Assert.Empty(AgentsIn(ProjectStatus.Implementation));
        Assert.Equal(new[] { "QA Agent", "Review Agent" }, AgentsIn(ProjectStatus.Testing));
        Assert.Empty(AgentsIn(ProjectStatus.Completed));
        Assert.Equal(1, byStage[(int)ProjectStatus.RequirementsGathering]!["stepsAwaitingDecision"]!.GetValue<int>());

        // The pending human decision is visible on the run.
        var item = summary["recentRuns"]![0]!;
        Assert.True(item["awaitingHumanDecision"]!.GetValue<bool>());
        Assert.Equal((int)ProjectStatus.RequirementsGathering, item["projectStatusAtStart"]!.GetValue<int>());
        Assert.Equal("Requirements Agent", item["currentStep"]!["displayName"]!.GetValue<string>());
        Assert.Equal(AwaitingApproval, item["currentStep"]!["status"]!.GetValue<int>());
        Assert.Equal(new[] { Roles.Administrator, Roles.BusinessAnalyst },
            item["currentStep"]!["approverRoles"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        Assert.Equal(6, item["steps"]!.AsArray().Count);

        // Approve one step, reject the next: both show up, and the lifecycle hasn't moved.
        run = await ApproveStepsAsync(run, 0, 1);
        Assert.Equal(200, (await Ba.Api.PostAsync($"/api/agent-runs/{Id(run)}/decision", new { approve = false, comment = "Wrong source" })).Status);

        (_, summary) = await Ba.Api.GetAsync($"/api/projects/{projectId}/agent-runs/summary");
        item = summary!["recentRuns"]![0]!;
        Assert.Equal(1, item["stepsApproved"]!.GetValue<int>());
        Assert.Equal(1, item["stepsRejected"]!.GetValue<int>());
        Assert.False(item["awaitingHumanDecision"]!.GetValue<bool>());
        Assert.Null(item["currentStep"]);
        Assert.Equal(StepRejected, item["steps"]![1]!["status"]!.GetValue<int>());
        Assert.Equal(Approved, item["steps"]![0]!["status"]!.GetValue<int>());
        Assert.Equal(1, summary["byLifecycleStage"]![(int)ProjectStatus.Analysis]!["stepsRejected"]!.GetValue<int>());
        Assert.Equal(1, summary["rejected"]!.GetValue<int>());
        Assert.Equal((int)ProjectStatus.RequirementsGathering, await StatusAsync(projectId));
    }
}
