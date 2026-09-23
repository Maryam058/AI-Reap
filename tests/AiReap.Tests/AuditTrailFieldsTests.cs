using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §27 — PromptTemplateVersion, ProducedArtifactId, and Accepted/Rejected were schema-only
// before this pass (always null/unset). This confirms all three are now actually wired:
// one AIExecution row per produced artifact (GenerationSupport.AddExecutions), each carrying
// its service's prompt version and a link back to the artifact it produced, and Accepted
// flipping to true/false once a human reviews that specific artifact.
public class AuditTrailFieldsTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<JsonNode> AuditRowForArtifactAsync(string projectId, string artifactId)
    {
        var (status, audit) = await _ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.Equal(200, status);
        return audit!.AsArray().Single(e => e!["producedArtifactId"]?.GetValue<string>() == artifactId)!;
    }

    [Fact]
    public async Task Generation_logs_one_execution_per_produced_artifact_with_prompt_version_and_no_decision_yet()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Audit", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        var sourceId = Id(source);

        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        var frId = Id(generated!["functionalRequirements"]![0]);
        var nfrId = Id(generated["nonFunctionalRequirements"]![0]);

        var frRow = await AuditRowForArtifactAsync(projectId, frId);
        var nfrRow = await AuditRowForArtifactAsync(projectId, nfrId);

        // one execution per artifact, not one execution for the whole call
        Assert.NotEqual(frRow["id"]!.GetValue<string>(), nfrRow["id"]!.GetValue<string>());
        Assert.Equal("RequirementGeneration", frRow["operationType"]!.GetValue<string>());
        Assert.Equal("RequirementGeneration-v1", frRow["promptTemplateVersion"]!.GetValue<string>());
        Assert.Equal("RequirementGeneration-v1", nfrRow["promptTemplateVersion"]!.GetValue<string>());
        Assert.Null(frRow["accepted"]);
        Assert.Null(nfrRow["accepted"]);
        // both rows carry the same underlying AI response (one call, fanned out per artifact)
        Assert.Equal(frRow["outputJson"]!.GetValue<string>(), nfrRow["outputJson"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approving_or_rejecting_an_artifact_sets_accepted_on_its_own_execution_only()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Audit2", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        Assert.InRange((await _host.AddMemberAsync(_ba, projectId, _reviewer)).Status, 200, 201);
        var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        var sourceId = Id(source);

        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        var frId = Id(generated!["functionalRequirements"]![0]);
        var nfrId = Id(generated["nonFunctionalRequirements"]![0]);

        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = 3 })).Status); // Approved
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{nfrId}/status", new { status = 4 })).Status); // Rejected

        var frRow = await AuditRowForArtifactAsync(projectId, frId);
        var nfrRow = await AuditRowForArtifactAsync(projectId, nfrId);
        Assert.True(frRow["accepted"]!.GetValue<bool>());
        Assert.False(nfrRow["accepted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_call_that_produces_no_artifact_still_logs_exactly_one_execution_with_no_produced_artifact_id()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Audit3", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        var (ss, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        var sourceId = Id(source);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);

        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze-quality")).Status);

        var (auditStatus, audit) = await _ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.Equal(200, auditStatus);
        var qualityRows = audit!.AsArray().Where(e => e!["operationType"]!.GetValue<string>() == "RequirementQualityAnalysis").ToList();
        var qualityRow = Assert.Single(qualityRows);
        Assert.Equal("RequirementQualityAnalysis-v1", qualityRow!["promptTemplateVersion"]!.GetValue<string>());
        Assert.Null(qualityRow["producedArtifactId"]);
        Assert.Null(qualityRow["accepted"]);
    }
}
