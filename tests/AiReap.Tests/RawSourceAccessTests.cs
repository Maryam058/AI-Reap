using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// S2 - a Developer works from the structured artifacts, never from raw requirement input. The raw
// requirement source and the project documents (and anything quoted from them: text, file names,
// document excerpts, the AI's analysis of the source) are withheld on every route, while approved
// requirements and the technical design stay visible. Other roles keep their access.
public class RawSourceAccessTests : IAsyncLifetime
{
    private const int Approved = 3;
    private const int FunctionalRequirement = 0, DesignArtifact = 6, ApiSpecification = 7, DataEntity = 8, ImplementationTask = 9;

    // Distinctive text that exists only in the raw input, so any leak is a plain substring match.
    private const string SourceMarker = "QUILLFEATHER-7731";
    private const string SourceFile = "quillfeather-brief.txt";
    private const string DocumentText = "Payroll batches QUILLDOC-4412 close every second Friday before noon.";
    private const string DocumentFile = "quilldoc-appendix.txt";

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

    private sealed record Fixture(string ProjectId, string SourceId, string RunId, string ApprovedFr, string UnapprovedRequirement, IReadOnlyList<string> Technical);

    private async Task<string> UploadAsync(string path, string fileName, string text)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", fileName);
        var response = await _ba.Http.PostAsync(path, form);
        Assert.InRange((int)response.StatusCode, 200, 201);
        return Id(await response.Content.ReadFromJsonAsync<JsonNode>());
    }

    // An uploaded requirement source and project document, an agent run whose Requirements step has
    // analysed the source, the generated requirements (one approved) and the technical design.
    private async Task<Fixture> BuildAsync()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Payroll", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        foreach (var member in new[] { _reviewer, _dev, _qa })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, projectId, member)).Status, 200, 201);
        }

        var sourceId = await UploadAsync($"/api/projects/{projectId}/requirement-sources/upload", SourceFile,
            $"Employees submit leave requests; managers approve them. Internal codename {SourceMarker}.");
        await UploadAsync($"/api/projects/{projectId}/documents/upload", DocumentFile, DocumentText);

        var (rs, run) = await _ba.PostAsync("/api/agent-runs", new { requirementSourceId = sourceId });
        Assert.Equal(200, rs);

        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        var approvedFr = Id(generated!["functionalRequirements"]![0]);
        var unapprovedNfr = Id(generated["nonFunctionalRequirements"]![0]);
        foreach (var step in new[] { "generate-design", "generate-data-entities", "generate-api-specs", "generate-tasks" })
        {
            Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/{step}")).Status);
        }

        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{approvedFr}/status", new { status = Approved, comment = "ok" })).Status);

        var technical = (await _ba.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray()
            .Where(a => new[] { DesignArtifact, ApiSpecification, DataEntity, ImplementationTask }.Contains(a!["artifactType"]!.GetValue<int>()))
            .Select(Id).ToList();
        Assert.NotEmpty(technical);

        return new Fixture(projectId, sourceId, Id(run), approvedFr, unapprovedNfr, technical);
    }

    private static void AssertNoRawSource(JsonNode? body, string route)
    {
        var json = body?.ToJsonString() ?? string.Empty;
        foreach (var leak in new[] { SourceMarker, SourceFile, "QUILLDOC-4412", DocumentFile })
        {
            Assert.False(json.Contains(leak, StringComparison.OrdinalIgnoreCase), $"{route} leaked '{leak}' to a Developer.");
        }
    }

    [Fact]
    public async Task Developer_cannot_read_a_raw_requirement_source()
    {
        var f = await BuildAsync();

        var (s, problem) = await _dev.GetAsync($"/api/requirement-sources/{f.SourceId}");

        Assert.Equal(403, s);
        Assert.Equal("forbidden", problem!["code"]!.GetValue<string>());
        AssertNoRawSource(problem, "GET requirement-source");
        Assert.Equal(404, (await _dev.GetAsync($"/api/requirement-sources/{Guid.NewGuid()}")).Status);
    }

    [Fact]
    public async Task Developer_source_list_carries_ids_only_no_text_or_file_name()
    {
        var f = await BuildAsync();

        var (s, list) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/requirement-sources");

        Assert.Equal(200, s);
        var entry = Assert.Single(list!.AsArray())!;
        Assert.Equal(f.SourceId, Id(entry));
        Assert.Equal(string.Empty, entry["rawText"]!.GetValue<string>());
        Assert.Null(entry["originalFileName"]);
        AssertNoRawSource(list, "GET requirement-sources");
    }

    [Fact]
    public async Task Developer_cannot_list_documents_and_the_copilot_gives_them_no_document_excerpts()
    {
        var f = await BuildAsync();

        var (ds, documents) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/documents");
        Assert.Equal(403, ds);
        AssertNoRawSource(documents, "GET documents");

        // The same question retrieves the excerpt for a BA - so the path is live - but not for a Developer.
        Assert.Equal(200, (await _ba.PostAsync($"/api/projects/{f.ProjectId}/copilot/ask", new { question = DocumentText })).Status);
        Assert.Contains("QUILLDOC-4412", _host.Ai.UserPrompts[^1]);

        var (cs, answer) = await _dev.PostAsync($"/api/projects/{f.ProjectId}/copilot/ask", new { question = DocumentText.Replace("QUILLDOC-4412 ", "") });
        Assert.Equal(200, cs);
        Assert.Empty(answer!["citations"]!.AsArray());
        // The context report says nothing about documents to a caller who may not read them.
        Assert.Null(answer["context"]!["excerptsOmitted"]);
        Assert.DoesNotContain("QUILLDOC-4412", _host.Ai.UserPrompts[^1]);
        Assert.DoesNotContain(SourceMarker, _host.Ai.UserPrompts[^1]);
        AssertNoRawSource(answer, "POST copilot/ask");
    }

    [Fact]
    public async Task Developer_project_summaries_carry_no_raw_source()
    {
        var f = await BuildAsync();

        foreach (var route in new[]
                 {
                     "/api/projects", $"/api/projects/{f.ProjectId}", $"/api/projects/{f.ProjectId}/dashboard",
                     "/api/projects/sdlc-progress", $"/api/projects/{f.ProjectId}/lifecycle", $"/api/projects/{f.ProjectId}/lifecycle/history",
                     "/api/notifications"
                 })
        {
            var (s, body) = await _dev.GetAsync(route);
            Assert.Equal(200, s);
            AssertNoRawSource(body, route);
            Assert.DoesNotContain(f.SourceId, body!.ToJsonString()); // project-level summaries don't point to it either
        }

        // These name what the Developer may see and may carry the source id as an opaque grouping
        // key (as the id-only source list does) - never its text or file name.
        foreach (var route in new[]
                 {
                     $"/api/projects/{f.ProjectId}/artifacts", $"/api/projects/{f.ProjectId}/traceability-matrix",
                     $"/api/projects/{f.ProjectId}/impact-notices", $"/api/projects/{f.ProjectId}/agent-runs/summary"
                 })
        {
            var (s, body) = await _dev.GetAsync(route);
            Assert.Equal(200, s);
            AssertNoRawSource(body, route);
        }
    }

    [Fact]
    public async Task Developer_ai_audit_trail_neither_quotes_nor_points_to_the_raw_source()
    {
        var f = await BuildAsync();

        var (s, audit) = await _dev.GetAsync($"/api/projects/{f.ProjectId}/ai-executions");

        Assert.Equal(200, s);
        Assert.NotEmpty(audit!.AsArray());
        AssertNoRawSource(audit, "GET ai-executions");
        Assert.DoesNotContain(f.SourceId, audit.ToJsonString());
        var visible = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts")).Body!.AsArray().Select(Id).ToHashSet();
        Assert.All(audit.AsArray(), e =>
        {
            var input = e!["inputReference"]?.GetValue<string>();
            Assert.True(input is null || visible.Contains(input), $"inputReference {input} is not a visible artifact");
        });

        // The BA's audit trail is unchanged: generation rows name the source they read.
        var baAudit = (await _ba.GetAsync($"/api/projects/{f.ProjectId}/ai-executions")).Body!.AsArray();
        Assert.Contains(baAudit, e => e!["inputReference"]?.GetValue<string>() == f.SourceId);
    }

    [Fact]
    public async Task Developer_agent_run_withholds_the_requirements_agents_analysis_of_the_source()
    {
        var f = await BuildAsync();
        static JsonArray Findings(JsonNode run) => run["stages"]![0]!["output"]!["findings"]!.AsArray();

        var baRun = (await _ba.GetAsync($"/api/agent-runs/{f.RunId}")).Body!;
        Assert.Contains(Findings(baRun), x => x!.GetValue<string>().StartsWith("Actors:"));

        var (s, devRun) = await _dev.GetAsync($"/api/agent-runs/{f.RunId}");
        Assert.Equal(200, s);
        var finding = Assert.Single(Findings(devRun!))!.GetValue<string>();
        Assert.Equal($"{Findings(baRun).Count} finding(s) not shown: they analyse the raw requirement source.", finding);
        Assert.Equal(baRun["stages"]![0]!["status"]!.GetValue<int>(), devRun!["stages"]![0]!["status"]!.GetValue<int>());
        AssertNoRawSource(devRun, "GET agent-run");

        var listed = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/agent-runs")).Body!.AsArray().Single()!;
        Assert.Single(Findings(listed));
    }

    [Fact]
    public async Task Business_analyst_reviewer_and_administrator_keep_raw_source_access()
    {
        var f = await BuildAsync();

        foreach (var user in new[] { _ba, _reviewer, _admin })
        {
            var (s, source) = await user.GetAsync($"/api/requirement-sources/{f.SourceId}");
            Assert.Equal(200, s);
            Assert.Contains(SourceMarker, source!["rawText"]!.GetValue<string>());
            Assert.Equal(SourceFile, source["originalFileName"]!.GetValue<string>());

            var list = (await user.GetAsync($"/api/projects/{f.ProjectId}/requirement-sources")).Body!;
            Assert.Contains(SourceMarker, list.ToJsonString());

            var (ds, documents) = await user.GetAsync($"/api/projects/{f.ProjectId}/documents");
            Assert.Equal(200, ds);
            Assert.Equal(DocumentFile, Assert.Single(documents!.AsArray())!["fileName"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Developer_still_sees_approved_requirements_and_the_technical_design()
    {
        var f = await BuildAsync();

        var visible = (await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts")).Body!.AsArray();
        var ids = visible.Select(Id).ToHashSet();
        Assert.Contains(f.ApprovedFr, ids);
        Assert.DoesNotContain(f.UnapprovedRequirement, ids);
        Assert.All(f.Technical, id => Assert.Contains(id, ids)); // G3: in every status

        var (fs, fr) = await _dev.GetAsync($"/api/artifacts/{f.ApprovedFr}");
        Assert.Equal(200, fs);
        Assert.Equal(FunctionalRequirement, fr!["artifactType"]!.GetValue<int>());
        Assert.Equal(404, (await _dev.GetAsync($"/api/artifacts/{f.UnapprovedRequirement}")).Status);

        // The workspace can still group what they see by source (ids only, see the list test).
        Assert.Equal(200, (await _dev.GetAsync($"/api/projects/{f.ProjectId}/artifacts?requirementSourceId={f.SourceId}")).Status);

        // G3: the Developer still reviews the design technically.
        var design = visible.First(a => a!["artifactType"]!.GetValue<int>() == DesignArtifact)!;
        Assert.Equal(201, (await _dev.PostAsync($"/api/artifacts/{Id(design)}/reviews", new { comment = "Technically sound." })).Status);
    }
}
