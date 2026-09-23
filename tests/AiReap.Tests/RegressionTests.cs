using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// Phase 1-3 behaviour, exercised through the same public API the UI uses, driven directly (not via agents).
// Checks that adding the agent layer did not change how the underlying services behave.
public class RegressionTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _dev = null!, _qa = null!, _reviewer = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _dev = await _host.RegisterAsync("Developer");
        _qa = await _host.RegisterAsync("QA");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _admin = await _host.RegisterAsync("Administrator");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<(string ProjectId, string SourceId)> ProjectWithSourceAsync()
    {
        var (s, p) = await _ba.PostAsync("/api/projects", new { name = "Reg", description = "d" });
        Assert.Equal(201, s);
        var projectId = Id(p);

        // §38 DoD — project membership. _ba is already a member (the creator); the other roles
        // this test class exercises against this project (approving, reading dashboard/audit/
        // copilot) have to be added explicitly.
        foreach (var member in new[] { _dev, _qa, _reviewer })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, projectId, member)).Status, 200, 201);
        }

        var (ss, src) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        return (projectId, Id(src));
    }

    // ---- Auth ----------------------------------------------------------------------------

    [Fact]
    public async Task Auth_rejects_bad_credentials_missing_tokens_and_weak_passwords()
    {
        var anon = new ApiUser("anon", _host.Factory.CreateClient());
        Assert.Equal(401, (await anon.GetAsync("/api/projects")).Status);
        Assert.Equal(401, (await anon.PostAsync("/api/auth/login", new { email = "nobody@test.io", password = "Wrong-pass1" })).Status);
        Assert.Equal(400, (await anon.PostAsync("/api/auth/register",
            new { email = "y@test.io", password = "short", displayName = "y" })).Status);
    }

    // ---- Projects ------------------------------------------------------------------------

    [Fact]
    public async Task Projects_crud_status_lifecycle_and_role_gating()
    {
        Assert.Equal(403, (await _dev.PostAsync("/api/projects", new { name = "nope" })).Status);
        Assert.Equal(403, (await _qa.PostAsync("/api/projects", new { name = "nope" })).Status);

        var (cs, p) = await _ba.PostAsync("/api/projects", new { name = "Alpha", description = "first" });
        Assert.Equal(201, cs);
        var id = Id(p);
        Assert.InRange((await _host.AddMemberAsync(_ba, id, _dev)).Status, 200, 201);

        var (ls, list) = await _dev.GetAsync("/api/projects"); // read is open to every role, once a member
        Assert.Equal(200, ls);
        Assert.Contains(list!.AsArray(), x => Id(x) == id);
        Assert.Equal(200, (await _dev.GetAsync($"/api/projects/{id}")).Status);
        // 403, not 404: ProjectMembershipFilter checks membership before the controller can
        // check existence, so a random id and a real id you're not a member of are
        // indistinguishable from the caller's side - the same non-leaking pattern this app
        // already uses for role-based authorization failures elsewhere.
        Assert.Equal(403, (await _ba.GetAsync($"/api/projects/{Guid.NewGuid()}")).Status);

        var (us, updated) = await _ba.SendAsync(HttpMethod.Put, $"/api/projects/{id}", new { name = "Alpha2", description = "changed" });
        Assert.Equal(200, us);
        Assert.Equal("Alpha2", updated!["name"]!.GetValue<string>());
        Assert.Equal(403, (await _dev.SendAsync(HttpMethod.Put, $"/api/projects/{id}", new { name = "hack" })).Status);

        // status may only advance one step; skipping ahead and going back are rejected
        Assert.Equal(200, (await _ba.SendAsync(HttpMethod.Patch, $"/api/projects/{id}/status", new { status = 1 })).Status);
        Assert.Equal(400, (await _ba.SendAsync(HttpMethod.Patch, $"/api/projects/{id}/status", new { status = 4 })).Status);
        Assert.Equal(400, (await _ba.SendAsync(HttpMethod.Patch, $"/api/projects/{id}/status", new { status = 0 })).Status);

        // stakeholders
        var (ss, sh) = await _ba.PostAsync($"/api/projects/{id}/stakeholders", new { name = "Sam", roleInProject = "Sponsor" });
        Assert.InRange(ss, 200, 201);
        Assert.Single((await _dev.GetAsync($"/api/projects/{id}/stakeholders")).Body!.AsArray());
        Assert.Equal(403, (await _dev.PostAsync($"/api/projects/{id}/stakeholders", new { name = "X" })).Status);
        Assert.Equal(204, (await _ba.SendAsync(HttpMethod.Delete, $"/api/projects/{id}/stakeholders/{Id(sh)}")).Status);
        Assert.Empty((await _dev.GetAsync($"/api/projects/{id}/stakeholders")).Body!.AsArray());
    }

    // ---- Requirements + AI pipeline (Phase 1-3, manual path) ------------------------------

    [Fact]
    public async Task Manual_pipeline_produces_linked_artifacts_and_the_approval_lifecycle_works()
    {
        var (projectId, sourceId) = await ProjectWithSourceAsync();

        // role gating on generators
        Assert.Equal(403, (await _dev.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);
        Assert.Equal(403, (await _qa.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);

        var (an, analysis) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, an);
        var questions = analysis!["clarificationQuestions"]!.AsArray();
        Assert.NotEmpty(questions);

        // answering a clarification versions the artifact and marks the human origin
        // artifact `data` must be camelCase, as the web client reads it (a PascalCase payload crashed the UI)
        Assert.Equal("Open", questions[0]!["data"]!["clarificationStatus"]!.GetValue<string>());
        Assert.Null(questions[0]!["data"]!["ClarificationStatus"]);
        var qid = Id(questions[0]);
        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{qid}/clarification-answer", new { answer = "Yes, delegation allowed.", notApplicable = false })).Status);
        var versions = (await _ba.GetAsync($"/api/artifacts/{qid}/versions")).Body!.AsArray();
        Assert.Equal(2, versions.Count);
        var v2 = versions.Single(v => v!["versionNumber"]!.GetValue<int>() == 2)!;
        Assert.Equal("Answered", v2["data"]!["clarificationStatus"]!.GetValue<string>());
        Assert.Equal(1, versions.Count(v => v!["origin"]!.GetValue<int>() == 1)); // exactly one human-origin version

        var (gs, gen) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        var frId = Id(gen!["functionalRequirements"]![0]);
        Assert.Equal("FR-001", gen["functionalRequirements"]![0]!["code"]!.GetValue<string>());
        Assert.NotNull(gen["functionalRequirements"]![0]!["data"]!["expectedResult"]);
        // NFR targets are always proposed assumptions, never trusted from the model
        Assert.Contains("proposed", gen["nonFunctionalRequirements"]![0]!["data"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-business-rules")).Status);
        var (us, stories) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        Assert.Equal(200, us);
        var storyId = Id(stories![0]);
        var (acs, criteria) = await _ba.PostAsync($"/api/artifacts/{storyId}/generate-acceptance-criteria");
        Assert.Equal(200, acs);
        Assert.NotEmpty(criteria!.AsArray());

        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-design")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-data-entities")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-api-specs")).Status);
        var (ts, tasks) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-tasks");
        Assert.Equal(200, ts);
        var taskId = Id(tasks![0]);
        var (tcs, cases) = await _ba.PostAsync($"/api/artifacts/{frId}/generate-test-cases");
        Assert.Equal(200, tcs);
        var caseIds = cases!.AsArray().Select(Id).ToList();
        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze-quality")).Status);
        Assert.Equal(200, (await _ba.PostAsync($"/api/projects/{projectId}/detect-conflicts")).Status);

        // traceability: one FR rolls up everything derived from it
        var (trs, matrix) = await _dev.GetAsync($"/api/projects/{projectId}/traceability-matrix");
        Assert.Equal(200, trs);
        var row = matrix!.AsArray().Single()!;
        Assert.NotEmpty(row["userStories"]!.AsArray());
        Assert.NotEmpty((row["tasks"] ?? row["implementationTasks"])!.AsArray());
        Assert.Equal(2, (row["testCases"]!.AsArray()).Count);

        // approval workflow: Developer cannot approve; Reviewer can; a review row is written
        Assert.Equal(403, (await _dev.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = 3 })).Status);
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = 3, comment = "ok" })).Status);
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactReviews"));

        // completion signals: approving the sole task promotes the FR to Implemented (5);
        // approving all its test cases promotes it to Verified (6)
        await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{taskId}/status", new { status = 3 });
        Assert.Equal(5, (await _dev.GetAsync($"/api/artifacts/{frId}")).Body!["status"]!.GetValue<int>());
        await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{caseIds[0]}/status", new { status = 3 });
        Assert.Equal(5, (await _dev.GetAsync($"/api/artifacts/{frId}")).Body!["status"]!.GetValue<int>());
        await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{caseIds[1]}/status", new { status = 3 });
        Assert.Equal(6, (await _dev.GetAsync($"/api/artifacts/{frId}")).Body!["status"]!.GetValue<int>());

        // impact analysis is advisory and never mutates
        var before = await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactVersions");
        var (imp, _) = await _dev.GetAsync($"/api/artifacts/{frId}/impact");
        Assert.Equal(200, imp);
        Assert.Equal(before, await _host.ScalarAsync("SELECT COUNT(*) FROM ArtifactVersions"));

        // dashboard and audit trail reflect the work
        var (ds, dash) = await _dev.GetAsync($"/api/projects/{projectId}/dashboard");
        Assert.Equal(200, ds);
        Assert.Equal(1, dash!["functionalRequirementCount"]!.GetValue<int>());
        Assert.True(dash["approvedCount"]!.GetValue<int>() >= 1);
        var (au, audit) = await _dev.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.Equal(200, au);
        Assert.True(audit!.AsArray().Count >= 8);
        Assert.DoesNotContain("reasoning", audit.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Requirement intake / documents / copilot ------------------------------------------

    [Fact]
    public async Task Source_upload_document_ingest_and_copilot_answer()
    {
        var (projectId, _) = await ProjectWithSourceAsync();

        // TXT upload as a requirement source
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Customers must be able to reset their password by email."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "req.txt");
        var up = await _ba.Http.PostAsync($"/api/projects/{projectId}/requirement-sources/upload", form);
        Assert.InRange((int)up.StatusCode, 200, 201);

        // unsupported extension rejected
        using var bad = new MultipartFormDataContent();
        bad.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "req.xyz");
        Assert.Equal(400, (int)(await _ba.Http.PostAsync($"/api/projects/{projectId}/requirement-sources/upload", bad)).StatusCode);

        // document -> chunks with embeddings -> copilot
        using var doc = new MultipartFormDataContent();
        var dfile = new ByteArrayContent(Encoding.UTF8.GetBytes("Password reset emails expire after 30 minutes."));
        dfile.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        doc.Add(dfile, "file", "notes.txt");
        Assert.InRange((int)(await _ba.Http.PostAsync($"/api/projects/{projectId}/documents/upload", doc)).StatusCode, 200, 201);
        Assert.True(await _host.ScalarAsync("SELECT COUNT(*) FROM DocumentChunks") >= 1);

        var (cs, answer) = await _dev.PostAsync($"/api/projects/{projectId}/copilot/ask", new { question = "How long do reset emails last?" });
        Assert.Equal(200, cs);
        Assert.False(string.IsNullOrWhiteSpace(answer!["answer"]?.GetValue<string>()));
        Assert.Equal(400, (await _dev.PostAsync($"/api/projects/{projectId}/copilot/ask", new { question = "" })).Status);
    }
}
