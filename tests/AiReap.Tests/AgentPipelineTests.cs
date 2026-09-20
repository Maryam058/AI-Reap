using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Xunit;
using static AiReap.Tests.AgentStatus;

namespace AiReap.Tests;

public class AgentPipelineTests : IAsyncLifetime
{
    // Stage order and the (only) non-admin role allowed to approve it.
    private static readonly (string Agent, string Approver)[] Stages =
    {
        ("Requirements Agent", "BusinessAnalyst"),
        ("Analysis Agent", "BusinessAnalyst"),
        ("Architecture Agent", "Developer"),
        ("Development Planning Agent", "Developer"),
        ("QA Agent", "QA"),
        ("Review Agent", "Reviewer"),
    };

    private static readonly string[] NonAdminRoles = { "BusinessAnalyst", "Developer", "QA", "Reviewer" };

    private readonly TestHost _host = new();
    private Dictionary<string, ApiUser> _users = new();
    private ApiUser Admin => _users["Administrator"];
    private ApiUser Ba => _users["BusinessAnalyst"];
    private string _projectId = "", _sourceId = "";

    public async Task InitializeAsync()
    {
        foreach (var role in NonAdminRoles.Append("Administrator"))
            _users[role] = await _host.RegisterAsync(role);

        var (ps, project) = await Ba.PostAsync("/api/projects", new { name = "Complaints" });
        Assert.Equal(201, ps);
        _projectId = project!["id"]!.GetValue<string>();

        var (ss, source) = await Ba.PostAsync($"/api/projects/{_projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Customers submit complaints; agents triage and resolve them." });
        Assert.InRange(ss, 200, 201);
        _sourceId = source!["id"]!.GetValue<string>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<JsonNode> StartRunAsync()
    {
        var (s, run) = await Ba.PostAsync("/api/agent-runs", new { requirementSourceId = _sourceId });
        Assert.Equal(200, s);
        return run!;
    }

    private async Task<JsonNode> GetRunAsync(string id) => (await Ba.GetAsync($"/api/agent-runs/{id}")).Body!;

    private static int StageStatus(JsonNode run, int i) => run["stages"]![i]!["status"]!.GetValue<int>();
    private static string RunId(JsonNode run) => run["id"]!.GetValue<string>();
    private static int RunStatus(JsonNode run) => run["status"]!.GetValue<int>();

    private Task<int> CountArtifacts(string type) =>
        _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE ArtifactType = {type}");

    // ArtifactType enum values are stored as ints; FunctionalRequirement = 0, UserStory = 3, TestCase = 10.
    private const string FrType = "0", BusinessRuleType = "2", StoryType = "3", TaskType = "9", TestCaseType = "10";

    // ---- Database ------------------------------------------------------------------------

    [Fact]
    public async Task Migration_creates_agent_tables_with_correct_foreign_keys_and_index()
    {
        Assert.Equal(2, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN ('AgentRuns','AgentStageRuns')"));
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%AddAgentRuns'"));

        // AgentRuns -> Projects, AgentRuns -> RequirementSources, AgentStageRuns -> AgentRuns
        var fkSql = @"SELECT COUNT(*) FROM sys.foreign_keys fk
            JOIN sys.tables t ON fk.parent_object_id = t.object_id
            JOIN sys.tables r ON fk.referenced_object_id = r.object_id
            WHERE t.name = '{0}' AND r.name = '{1}' AND fk.delete_referential_action_desc = '{2}'";
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "AgentRuns", "Projects", "NO_ACTION")));
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "AgentRuns", "RequirementSources", "NO_ACTION")));
        Assert.Equal(1, await _host.ScalarAsync(string.Format(fkSql, "AgentStageRuns", "AgentRuns", "CASCADE")));

        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AgentStageRuns_AgentRunId_Order' AND is_unique = 1"));

        // concurrency guards: rowversion on stages, and one-active-run-per-source filtered unique index
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON c.user_type_id = t.user_type_id WHERE c.object_id = OBJECT_ID('AgentStageRuns') AND c.name = 'RowVersion' AND t.name = 'timestamp'"));
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AgentRuns_RequirementSourceId' AND is_unique = 1 AND has_filter = 1"));
    }

    // ---- Auth / API surface --------------------------------------------------------------

    [Fact]
    public async Task Agent_endpoints_require_authentication()
    {
        var anon = _host.Factory.CreateClient();
        Assert.Equal(401, (int)(await anon.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(401, (int)(await anon.GetAsync($"/api/projects/{_projectId}/agent-runs")).StatusCode);
        Assert.Equal(401, (int)(await anon.PostAsync("/api/agent-runs", JsonBody(new { requirementSourceId = _sourceId }))).StatusCode);
        Assert.Equal(401, (int)(await anon.PostAsync($"/api/agent-runs/{Guid.NewGuid()}/decision", JsonBody(new { approve = true }))).StatusCode);
        Assert.Equal(401, (int)(await anon.PostAsync($"/api/agent-runs/{Guid.NewGuid()}/retry", JsonBody(new { }))).StatusCode);
    }

    private static StringContent JsonBody(object o) =>
        new(System.Text.Json.JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Only_writers_can_start_a_run_and_unknown_ids_return_404()
    {
        foreach (var role in new[] { "Developer", "QA", "Reviewer" })
            Assert.Equal(403, (await _users[role].PostAsync("/api/agent-runs", new { requirementSourceId = _sourceId })).Status);

        Assert.Equal(404, (await Ba.PostAsync("/api/agent-runs", new { requirementSourceId = Guid.NewGuid() })).Status);
        Assert.Equal(404, (await Ba.GetAsync($"/api/agent-runs/{Guid.NewGuid()}")).Status);
        Assert.Equal(404, (await Ba.DecideAsync(Guid.NewGuid().ToString(), true)).Status);
        Assert.Equal(404, (await Ba.PostAsync($"/api/agent-runs/{Guid.NewGuid()}/retry")).Status);
        Assert.Equal(0, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentRuns"));
    }

    [Fact]
    public async Task Definitions_list_six_agents_in_order_with_approver_roles()
    {
        var (s, body) = await Ba.GetAsync("/api/agents");
        Assert.Equal(200, s);
        var names = body!.AsArray().Select(a => a!["displayName"]!.GetValue<string>()).ToArray();
        Assert.Equal(Stages.Select(x => x.Agent).ToArray(), names);
        Assert.DoesNotContain(names, n => n.Contains("Deploy", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Happy path + roles --------------------------------------------------------------

    [Fact]
    public async Task Happy_path_runs_all_six_stages_and_only_the_right_role_can_approve_each()
    {
        var run = await StartRunAsync();
        var id = RunId(run);

        for (var i = 0; i < Stages.Length; i++)
        {
            Assert.Equal(Stages[i].Agent, run["stages"]![i]!["displayName"]!.GetValue<string>());
            Assert.Equal(Awaiting, StageStatus(run, i));
            Assert.Equal(RunAwaiting, RunStatus(run));
            Assert.NotNull(run["stages"]![i]!["output"]);
            for (var j = i + 1; j < Stages.Length; j++) Assert.Equal(Pending, StageStatus(run, j));

            // every role other than the stage's approver (and admin) is refused, and refusal changes nothing
            foreach (var role in NonAdminRoles.Where(r => r != Stages[i].Approver))
            {
                Assert.Equal(403, (await _users[role].DecideAsync(id, true)).Status);
                Assert.Equal(403, (await _users[role].DecideAsync(id, false)).Status);
            }
            Assert.Equal(Awaiting, StageStatus(await GetRunAsync(id), i));

            var (s, next) = await _users[Stages[i].Approver].DecideAsync(id, true, $"approved {Stages[i].Agent}");
            Assert.Equal(200, s);
            run = next!;
            Assert.Equal(Approved, StageStatus(run, i));
            Assert.Equal("approved " + Stages[i].Agent, run["stages"]![i]!["decisionComment"]!.GetValue<string>());
        }

        Assert.Equal(RunCompleted, RunStatus(run));
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal(Approved, StageStatus(run, i)));

        // persisted in SQL, not just returned
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentRuns WHERE Status = 2"));
        Assert.Equal(6, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentStageRuns WHERE Status = 3 AND DecidedByUserId IS NOT NULL"));

        // artifacts produced exactly once each
        Assert.Equal(1, await CountArtifacts(FrType));
        Assert.Equal(1, await CountArtifacts(StoryType));
        Assert.Equal(1, await CountArtifacts(TaskType));
        Assert.Equal(2, await CountArtifacts(TestCaseType));

        // no decision possible on a completed run
        Assert.Equal(409, (await Ba.DecideAsync(id, true)).Status);
        Assert.Equal(409, (await Ba.PostAsync($"/api/agent-runs/{id}/retry")).Status);
    }

    [Fact]
    public async Task Administrator_can_approve_every_stage()
    {
        var run = await StartRunAsync();
        for (var i = 0; i < Stages.Length; i++)
        {
            var (s, next) = await Admin.DecideAsync(RunId(run), true);
            Assert.Equal(200, s);
            run = next!;
        }
        Assert.Equal(RunCompleted, RunStatus(run));
    }

    // ---- Reject --------------------------------------------------------------------------

    [Fact]
    public async Task Reject_halts_the_run_persists_the_comment_and_is_recoverable_by_a_new_run()
    {
        var run = await StartRunAsync();
        var id = RunId(run);
        Assert.Equal(200, (await Ba.DecideAsync(id, true)).Status); // Requirements approved -> Analysis awaiting
        Assert.Equal(1, await CountArtifacts(FrType));

        // a role that cannot approve Analysis cannot reject it either
        Assert.Equal(403, (await _users["Developer"].DecideAsync(id, false, "sneaky")).Status);
        Assert.Equal(Awaiting, StageStatus(await GetRunAsync(id), 1));

        var (s, rejected) = await Ba.DecideAsync(id, false, "Requirements are wrong");
        Assert.Equal(200, s);
        Assert.Equal(RunRejected, RunStatus(rejected!));
        Assert.Equal(Approved, StageStatus(rejected!, 0));
        Assert.Equal(Rejected, StageStatus(rejected!, 1));
        Assert.Equal("Requirements are wrong", rejected!["stages"]![1]!["decisionComment"]!.GetValue<string>());
        for (var j = 2; j < 6; j++) Assert.Equal(Pending, StageStatus(rejected!, j));

        // persisted, and no further stage ran
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM AgentStageRuns WHERE Status = 4 AND DecisionComment = 'Requirements are wrong'"));
        Assert.Equal(0, await _host.ScalarAsync("SELECT COUNT(*) FROM Artifacts WHERE ArtifactType IN (7,8,9,10)"));

        // pipeline does not continue: any further decision/retry is refused, even by an authorised role
        Assert.Equal(409, (await Ba.DecideAsync(id, true)).Status);
        Assert.Equal(409, (await Admin.DecideAsync(id, true)).Status);
        Assert.Equal(409, (await Ba.PostAsync($"/api/agent-runs/{id}/retry")).Status);
        Assert.Equal(Rejected, StageStatus(await GetRunAsync(id), 1));

        // recoverable: a new run may start and reuses (does not duplicate) existing artifacts
        var run2 = await StartRunAsync();
        Assert.Equal(Awaiting, StageStatus(run2, 0));
        Assert.Equal(200, (await Ba.DecideAsync(RunId(run2), true)).Status);
        Assert.Equal(1, await CountArtifacts(FrType));
    }

    // ---- Retry ---------------------------------------------------------------------------

    [Fact]
    public async Task Failed_stage_is_recorded_and_retry_continues_the_workflow_without_duplicates()
    {
        var run = await StartRunAsync();
        var id = RunId(run);

        _host.Ai.FailOnceWhenPromptContains("functionalRequirements"); // Analysis stage's first AI call
        var (s, failed) = await Ba.DecideAsync(id, true); // approve Requirements -> Analysis runs and fails
        Assert.Equal(200, s);
        Assert.Equal(RunFailed, RunStatus(failed!));
        Assert.Equal(Approved, StageStatus(failed!, 0));
        Assert.Equal(Failed, StageStatus(failed!, 1));
        Assert.Contains("Simulated AI provider failure", failed!["stages"]![1]!["error"]!.GetValue<string>());
        for (var j = 2; j < 6; j++) Assert.Equal(Pending, StageStatus(failed!, j));

        // persisted
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM AgentStageRuns WHERE Status = 5 AND Error LIKE '%Simulated AI provider failure%'"));
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentRuns WHERE Status = 4"));
        Assert.Equal(0, await CountArtifacts(FrType));

        // a failed stage cannot be approved, and a second run cannot start while one is failed
        Assert.Equal(409, (await Ba.DecideAsync(id, true)).Status);
        Assert.Equal(409, (await Ba.PostAsync("/api/agent-runs", new { requirementSourceId = _sourceId })).Status);

        // retry only by an authorised role
        Assert.Equal(403, (await _users["Developer"].PostAsync($"/api/agent-runs/{id}/retry")).Status);
        Assert.Equal(Failed, StageStatus(await GetRunAsync(id), 1));

        var (rs, retried) = await Ba.PostAsync($"/api/agent-runs/{id}/retry");
        Assert.Equal(200, rs);
        Assert.Equal(RunAwaiting, RunStatus(retried!));
        Assert.Equal(Awaiting, StageStatus(retried!, 1));
        Assert.Null(retried!["stages"]![1]!["error"]);
        Assert.Equal(Approved, StageStatus(retried!, 0));
        for (var j = 2; j < 6; j++) Assert.Equal(Pending, StageStatus(retried!, j));
        Assert.Equal(6, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentStageRuns")); // no duplicate stage rows
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentRuns"));

        // retry is not offered where nothing failed
        Assert.Equal(409, (await Ba.PostAsync($"/api/agent-runs/{id}/retry")).Status);

        // workflow continues to completion
        run = retried;
        for (var i = 1; i < Stages.Length; i++)
        {
            var (ds, next) = await _users[Stages[i].Approver].DecideAsync(id, true);
            Assert.Equal(200, ds);
            run = next!;
        }
        Assert.Equal(RunCompleted, RunStatus(run));
        Assert.Equal(1, await CountArtifacts(FrType));
    }

    [Fact]
    public async Task Retry_after_a_partial_failure_skips_finished_substeps_instead_of_duplicating()
    {
        var run = await StartRunAsync();
        var id = RunId(run);

        // Analysis: FRs and business rules succeed and are saved, then the story step fails.
        _host.Ai.FailOnceWhenPromptContains("userStories");
        var (_, failed) = await Ba.DecideAsync(id, true);
        Assert.Equal(Failed, StageStatus(failed!, 1));
        Assert.Equal(1, await CountArtifacts(FrType));
        Assert.Equal(1, await CountArtifacts(BusinessRuleType));
        Assert.Equal(0, await CountArtifacts(StoryType));

        var (_, retried) = await Ba.PostAsync($"/api/agent-runs/{id}/retry");
        Assert.Equal(Awaiting, StageStatus(retried!, 1));
        Assert.Equal(1, await CountArtifacts(FrType));            // not regenerated
        Assert.Equal(1, await CountArtifacts(BusinessRuleType));  // not regenerated
        Assert.Equal(1, await CountArtifacts(StoryType));         // completed on retry
    }

    [Fact]
    public async Task Failed_stage_can_be_abandoned_and_a_new_run_can_then_start()
    {
        var run = await StartRunAsync();
        var id = RunId(run);
        _host.Ai.FailOnceWhenPromptContains("functionalRequirements");
        await Ba.DecideAsync(id, true);

        Assert.Equal(403, (await _users["Developer"].DecideAsync(id, false)).Status);
        var (s, abandoned) = await Ba.DecideAsync(id, false, "giving up");
        Assert.Equal(200, s);
        Assert.Equal(RunRejected, RunStatus(abandoned!));
        Assert.Equal(Rejected, StageStatus(abandoned!, 1));

        Assert.Equal(200, (await Ba.PostAsync("/api/agent-runs", new { requirementSourceId = _sourceId })).Status);
    }

    // ---- Duplicate execution -------------------------------------------------------------

    [Fact]
    public async Task Concurrent_double_approval_does_not_execute_a_stage_twice()
    {
        var run = await StartRunAsync();
        var id = RunId(run);
        Assert.Equal(200, (await Ba.DecideAsync(id, true)).Status); // now Analysis awaiting

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Ba.DecideAsync(id, true)));

        // Exactly one request may advance the run; the rest must be refused.
        Assert.True(results.Count(r => r.Status == 200) == 1, "statuses: " + string.Join(",", results.Select(r => r.Status)));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.True(r.Status == 409, $"loser got {r.Status}: {r.Body?.ToJsonString()[..Math.Min(400, r.Body.ToJsonString().Length)]}"));

        // Whatever the responses, no artifact set was generated twice.
        Assert.Equal(1, await CountArtifacts(FrType));
        Assert.Equal(1, await CountArtifacts(StoryType));
        Assert.Equal(1, await CountArtifacts(BusinessRuleType));
        Assert.Equal(6, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentStageRuns"));
    }

    [Fact]
    public async Task Concurrent_start_creates_only_one_active_run()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Ba.PostAsync("/api/agent-runs", new { requirementSourceId = _sourceId })));

        Assert.True(results.Count(r => r.Status == 200) == 1, "statuses: " + string.Join(",", results.Select(r => r.Status)));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.True(r.Status == 409, $"loser got {r.Status}"));
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentRuns"));
        Assert.Equal(6, await _host.ScalarAsync("SELECT COUNT(*) FROM AgentStageRuns"));
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM Artifacts WHERE ArtifactType = 5 AND Code = 'CQ-001'"));
    }

    [Fact]
    public async Task Concurrent_retry_executes_the_failed_stage_once()
    {
        var run = await StartRunAsync();
        var id = RunId(run);
        _host.Ai.FailOnceWhenPromptContains("functionalRequirements");
        await Ba.DecideAsync(id, true);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Ba.PostAsync($"/api/agent-runs/{id}/retry")));

        Assert.True(results.Count(r => r.Status == 200) == 1, "statuses: " + string.Join(",", results.Select(r => r.Status)));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.True(r.Status == 409, $"loser got {r.Status}"));
        Assert.Equal(1, await CountArtifacts(FrType));
        Assert.Equal(Awaiting, StageStatus(await GetRunAsync(id), 1));
    }

    // ---- Persistence across restart ------------------------------------------------------

    [Fact]
    public async Task Run_state_is_reconstructed_from_sql_after_a_backend_restart()
    {
        var run = await StartRunAsync();
        var id = RunId(run);
        await Ba.DecideAsync(id, true, "carry on");

        await using var restarted = new TestHost(_host.DatabaseName); // brand-new server process, same database
        var client = restarted.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Ba.Http.DefaultRequestHeaders.Authorization!.Parameter);
        var ba2 = new ApiUser("BusinessAnalyst", client);

        var (s, reloaded) = await ba2.GetAsync($"/api/agent-runs/{id}");
        Assert.Equal(200, s);
        Assert.Equal(RunAwaiting, RunStatus(reloaded!));
        Assert.Equal(Approved, StageStatus(reloaded!, 0));
        Assert.Equal("carry on", reloaded!["stages"]![0]!["decisionComment"]!.GetValue<string>());
        Assert.Equal(Awaiting, StageStatus(reloaded!, 1));
        Assert.NotNull(reloaded["stages"]![1]!["output"]); // stage output survived, not just status

        var (ls, list) = await ba2.GetAsync($"/api/projects/{_projectId}/agent-runs");
        Assert.Equal(200, ls);
        Assert.Single(list!.AsArray());

        // and the restarted backend can continue the very same run
        var (ds, next) = await ba2.DecideAsync(id, true);
        Assert.Equal(200, ds);
        Assert.Equal(Awaiting, StageStatus(next!, 2));
    }
}
