using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §4/§27 - one AI call that produces several artifacts is logged as one AIExecution row per
// artifact, and every row stores the call's whole raw response. A Developer (ArtifactVisibility)
// may see some of those artifacts but not others, so for a Developer each row's output is only that
// row's own artifact, never the raw batch; a hidden artifact used as the call's input is withheld
// too. Administrator, Business Analyst, Reviewer and QA keep the complete raw response.
public class AuditTrailIsolationTests : IAsyncLifetime
{
    private const int Approved = 3;
    private const int FunctionalRequirement = 0, NonFunctionalRequirement = 1, BusinessRule = 2, UserStory = 3, AcceptanceCriterion = 4,
        DesignArtifact = 6, ApiSpecification = 7, DataEntity = 8, ImplementationTask = 9, TestCase = 10;

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!, _qa = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
        _qa = await _host.RegisterAsync("QA");
        _admin = await _host.RegisterAsync("Administrator");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private IEnumerable<ApiUser> Privileged => [_admin, _ba, _reviewer, _qa];

    private async Task<(string ProjectId, string SourceId)> ProjectAsync()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Audit isolation", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        foreach (var member in new[] { _reviewer, _dev, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, projectId, member)).Status, 200, 201);
        }

        var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them." });
        Assert.InRange(ss, 200, 201);
        return (projectId, Id(source));
    }

    private async Task<List<JsonNode>> ArtifactsAsync(string projectId, int type) =>
        (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray()
            .Where(a => a!["artifactType"]!.GetValue<int>() == type).Select(a => a!).OrderBy(a => a["code"]!.GetValue<string>()).ToList();

    private async Task ApproveAsync(string artifactId) =>
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{artifactId}/status", new { status = Approved, comment = "ok" })).Status);

    private static async Task<JsonArray> AuditAsync(ApiUser user, string projectId)
    {
        var (status, body) = await user.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.Equal(200, status);
        return body!.AsArray();
    }

    private static List<JsonNode> Rows(JsonArray audit, string operation) =>
        audit.Where(e => e!["operationType"]!.GetValue<string>() == operation).Select(e => e!).ToList();

    private static string Output(JsonNode row) => row["outputJson"]!.GetValue<string>();

    // Every string a hidden artifact could leak: code, title and each text value of its payload.
    private static IEnumerable<string> Fingerprint(JsonNode artifact)
    {
        yield return artifact["code"]!.GetValue<string>();
        yield return artifact["title"]!.GetValue<string>();
        foreach (var value in Strings(artifact["data"]))
        {
            if (value.Length >= 8)
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => []
    };

    // Text the Developer can already read through the artifact endpoints (codes, titles, content of
    // every artifact visible to them). A hidden artifact's fingerprint ignores these: e.g. an API
    // specification may legitimately share its title with the requirement it implements.
    private async Task<HashSet<string>> DeveloperVisibleTextAsync(string projectId) =>
        (await _dev.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray()
            .SelectMany(a => Fingerprint(a!)).ToHashSet();

    private async Task AssertHidesAsync(string projectId, JsonArray devAudit, JsonNode hidden)
    {
        var visible = await DeveloperVisibleTextAsync(projectId);
        Assert.DoesNotContain(hidden["code"]!.GetValue<string>(), visible);
        var text = devAudit.ToJsonString();
        foreach (var fragment in Fingerprint(hidden).Where(f => !visible.Contains(f)))
        {
            Assert.DoesNotContain(JsonEscape(fragment), text);
        }

        Assert.DoesNotContain(devAudit, e => e!["producedArtifactId"]?.GetValue<string>() == Id(hidden));
    }

    // The audit JSON carries each output as a JSON string, so compare in its escaped form.
    private static string JsonEscape(string s) => JsonValue.Create(s)!.ToJsonString()[1..^1];

    // A Developer's row carries only its own artifact: scope marker, that artifact's code and title.
    private static void AssertIsolated(JsonNode row, JsonNode own)
    {
        var output = JsonNode.Parse(Output(row))!;
        Assert.Equal("artifact", output["scope"]!.GetValue<string>());
        Assert.Equal(own["code"]!.GetValue<string>(), output["artifactCode"]!.GetValue<string>());
        Assert.Equal(own["title"]!.GetValue<string>(), output["title"]!.GetValue<string>());
        Assert.Equal(Id(own), row["producedArtifactId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requirements_batch_developer_gets_only_the_approved_requirement_and_never_the_nfr()
    {
        var (projectId, sourceId) = await ProjectAsync();
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        var fr = (await ArtifactsAsync(projectId, FunctionalRequirement)).Single();
        var nfr = (await ArtifactsAsync(projectId, NonFunctionalRequirement)).Single();
        await ApproveAsync(Id(fr));

        var devAudit = await AuditAsync(_dev, projectId);
        var row = Assert.Single(Rows(devAudit, "RequirementGeneration"));
        AssertIsolated(row, fr);
        var output = JsonNode.Parse(Output(row))!;
        Assert.Equal("Validate input and persist the request.", output["data"]!["processing"]!.GetValue<string>());

        await AssertHidesAsync(projectId, devAudit, nfr);
        Assert.DoesNotContain("nonFunctionalRequirements", devAudit.ToJsonString());
        Assert.DoesNotContain("respond to user actions promptly", devAudit.ToJsonString());

        // Everyone else still gets the complete raw response of the call, on both rows.
        foreach (var user in Privileged)
        {
            var rows = Rows(await AuditAsync(user, projectId), "RequirementGeneration");
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Contains("nonFunctionalRequirements", Output(r));
                Assert.Contains("Response time", Output(r));
                Assert.Contains("respond to user actions promptly", Output(r));
                Assert.Contains("functionalRequirements", Output(r));
                Assert.Equal(sourceId, r["inputReference"]!.GetValue<string>());
            });
        }
    }

    [Fact]
    public async Task Acceptance_criteria_batch_developer_gets_one_criterion_and_neither_the_other_nor_the_hidden_story()
    {
        var (projectId, sourceId) = await ProjectAsync();
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories")).Status);
        var story = (await ArtifactsAsync(projectId, UserStory)).Single();
        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{Id(story)}/generate-acceptance-criteria")).Status);
        var criteria = await ArtifactsAsync(projectId, AcceptanceCriterion);
        Assert.Equal(2, criteria.Count);
        var visible = criteria.Single(c => c["title"]!.GetValue<string>() == "Valid submission");
        var hidden = criteria.Single(c => c["title"]!.GetValue<string>() == "Missing required field");
        await ApproveAsync(Id(visible)); // the story itself stays unapproved

        var devAudit = await AuditAsync(_dev, projectId);
        var row = Assert.Single(Rows(devAudit, "AcceptanceCriteriaGeneration"));
        AssertIsolated(row, visible);
        Assert.Equal("they submit a valid request", JsonNode.Parse(Output(row))!["data"]!["when"]!.GetValue<string>());
        // The call's input is the (hidden) story: its id is withheld.
        Assert.Null(row["inputReference"]);

        await AssertHidesAsync(projectId, devAudit, hidden);
        await AssertHidesAsync(projectId, devAudit, story);
        Assert.DoesNotContain(Id(story), devAudit.ToJsonString());
        Assert.DoesNotContain("acceptanceCriteria", devAudit.ToJsonString());

        foreach (var user in Privileged)
        {
            var rows = Rows(await AuditAsync(user, projectId), "AcceptanceCriteriaGeneration");
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Contains("Missing required field", Output(r));
                Assert.Contains("Valid submission", Output(r));
                Assert.Equal(Id(story), r["inputReference"]!.GetValue<string>());
            });
        }
    }

    [Fact]
    public async Task Test_case_batch_developer_gets_only_the_approved_test_case()
    {
        var (projectId, sourceId) = await ProjectAsync();
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        var fr = (await ArtifactsAsync(projectId, FunctionalRequirement)).Single();
        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{Id(fr)}/generate-test-cases")).Status);
        var tests = await ArtifactsAsync(projectId, TestCase);
        Assert.Equal(2, tests.Count);
        var visible = tests.Single(t => t["title"]!.GetValue<string>() == "Submit a valid request");
        var hidden = tests.Single(t => t["title"]!.GetValue<string>() == "Submit with missing required field");
        await ApproveAsync(Id(fr));
        await ApproveAsync(Id(visible));

        var devAudit = await AuditAsync(_dev, projectId);
        var row = Assert.Single(Rows(devAudit, "TestCaseGeneration"));
        AssertIsolated(row, visible);
        Assert.Equal(Id(fr), row["inputReference"]!.GetValue<string>()); // the approved FR is visible input
        await AssertHidesAsync(projectId, devAudit, hidden);
        Assert.DoesNotContain("testCases", devAudit.ToJsonString());

        foreach (var user in Privileged)
        {
            var rows = Rows(await AuditAsync(user, projectId), "TestCaseGeneration");
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Contains("Leave a required field blank", Output(r)));
        }
    }

    [Fact]
    public async Task Story_and_rule_batches_expose_only_the_approved_artifact_never_the_hidden_requirement_they_reference()
    {
        var (projectId, sourceId) = await ProjectAsync();
        foreach (var step in new[] { "generate-requirements", "generate-user-stories", "generate-business-rules" })
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        var fr = (await ArtifactsAsync(projectId, FunctionalRequirement)).Single();
        var nfr = (await ArtifactsAsync(projectId, NonFunctionalRequirement)).Single();
        var story = (await ArtifactsAsync(projectId, UserStory)).Single();
        var rule = (await ArtifactsAsync(projectId, BusinessRule)).Single();
        await ApproveAsync(Id(story));
        await ApproveAsync(Id(rule)); // the FR and NFR stay unapproved

        var devAudit = await AuditAsync(_dev, projectId);
        AssertIsolated(Assert.Single(Rows(devAudit, "UserStoryGeneration")), story);
        AssertIsolated(Assert.Single(Rows(devAudit, "BusinessRuleExtraction")), rule);
        Assert.Empty(Rows(devAudit, "RequirementGeneration"));
        await AssertHidesAsync(projectId, devAudit, fr);
        await AssertHidesAsync(projectId, devAudit, nfr);
        Assert.DoesNotContain("relatedRequirementCodes", devAudit.ToJsonString());

        foreach (var user in Privileged)
        {
            var audit = await AuditAsync(user, projectId);
            Assert.Contains("relatedRequirementCodes", Output(Rows(audit, "UserStoryGeneration").Single()));
            Assert.Contains("relatedRequirementCodes", Output(Rows(audit, "BusinessRuleExtraction").Single()));
        }
    }

    [Fact]
    public async Task Technical_batches_are_visible_before_approval_but_never_carry_the_hidden_requirement_they_reference()
    {
        var (projectId, sourceId) = await ProjectAsync();
        foreach (var step in new[] { "generate-requirements", "generate-design", "generate-data-entities", "generate-api-specs", "generate-tasks" })
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        var fr = (await ArtifactsAsync(projectId, FunctionalRequirement)).Single();
        var nfr = (await ArtifactsAsync(projectId, NonFunctionalRequirement)).Single();
        var devAudit = await AuditAsync(_dev, projectId);

        // Technical artifacts are visible to a Developer while still AI drafts (G3): their rows are
        // returned, each isolated to its own artifact.
        foreach (var (operation, type) in new[]
                 {
                     ("SolutionDesignGeneration", DesignArtifact), ("DatabaseDesignGeneration", DataEntity),
                     ("ApiDesignGeneration", ApiSpecification), ("ImplementationPlanning", ImplementationTask)
                 })
        {
            var own = (await ArtifactsAsync(projectId, type)).Single();
            AssertIsolated(Assert.Single(Rows(devAudit, operation)), own);
        }

        // The raw responses name the (unapproved) FR; the Developer's copies do not.
        await AssertHidesAsync(projectId, devAudit, fr);
        await AssertHidesAsync(projectId, devAudit, nfr);
        Assert.DoesNotContain("relatedRequirementCodes", devAudit.ToJsonString());
        Assert.Empty(Rows(devAudit, "RequirementGeneration"));

        foreach (var user in Privileged)
        {
            var audit = await AuditAsync(user, projectId);
            foreach (var operation in new[] { "DatabaseDesignGeneration", "ApiDesignGeneration", "ImplementationPlanning" })
            {
                Assert.Contains("relatedRequirementCodes", Output(Rows(audit, operation).Single()));
            }

            Assert.Equal(2, Rows(audit, "RequirementGeneration").Count);
        }
    }

    [Fact]
    public async Task Developer_never_receives_a_raw_response_on_any_row()
    {
        var (projectId, sourceId) = await ProjectAsync();
        foreach (var step in new[] { "analyze", "generate-requirements", "generate-user-stories", "generate-business-rules", "generate-design", "generate-data-entities", "generate-api-specs", "generate-tasks", "analyze-quality" })
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        Assert.Equal(200, (await _ba.PostAsync($"/api/projects/{projectId}/detect-conflicts")).Status);
        foreach (var artifact in (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray())
        {
            await ApproveAsync(Id(artifact));
        }

        var baAudit = await AuditAsync(_ba, projectId);
        var devAudit = await AuditAsync(_dev, projectId);
        Assert.NotEmpty(devAudit);
        var rawOutputs = baAudit.Select(e => Output(e!)).ToHashSet();
        foreach (var row in devAudit)
        {
            Assert.DoesNotContain(Output(row!), rawOutputs);
            Assert.Equal("artifact", JsonNode.Parse(Output(row!))!["scope"]!.GetValue<string>());
        }

        // Calls that produced no artifact (quality analysis, conflict detection) are not returned to a Developer.
        Assert.DoesNotContain(devAudit, e => e!["producedArtifactId"] is null);
        Assert.Contains(baAudit, e => e!["operationType"]!.GetValue<string>() == "RequirementQualityAnalysis");
    }
}
