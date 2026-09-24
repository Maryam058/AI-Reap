using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §21 Business Objective traceability, §22 automatic impact notices, §25 Copilot grounding and
// §26 RAG in generation. Stub-backed (no live Gemini); prompt assertions use the recorded prompts.
public class Phase3TraceabilityTests : IAsyncLifetime
{
    private const int Approved = 3, UnderReview = 2;

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<(string ProjectId, string SourceId)> ProjectAsync(string? objectives = "- Reduce leave approval time\n- Give employees visibility of their balance")
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Leave", description = "d", objectives });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        await _host.AddMemberAsync(_ba, projectId, _reviewer);
        await _host.AddMemberAsync(_ba, projectId, _dev);
        var (_, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them. Login uses company SSO authentication." });
        return (projectId, Id(source));
    }

    // ---- §21 Business Objective -> Requirement ------------------------------------------------

    [Fact]
    public async Task Project_objectives_become_business_objective_artifacts_and_head_the_traceability_matrix()
    {
        var (projectId, sourceId) = await ProjectAsync();

        var objectives = (await _ba.GetAsync($"/api/projects/{projectId}/business-objectives")).Body!.AsArray();
        Assert.Equal(new[] { "BO-001", "BO-002" }, objectives.Select(o => o!["code"]!.GetValue<string>()).ToArray());
        Assert.Equal("Reduce leave approval time", objectives[0]!["title"]!.GetValue<string>());

        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        Assert.Contains(_host.Ai.UserPrompts, p => p.Contains("BO-001: Reduce leave approval time"));

        var row = (await _ba.GetAsync($"/api/projects/{projectId}/traceability-matrix")).Body!.AsArray().Single()!;
        Assert.Equal("BO-001", row["businessObjectives"]!.AsArray().Single()!["code"]!.GetValue<string>());

        // Objective -> requirement coverage (BO-002 has none: a visible gap).
        objectives = (await _ba.GetAsync($"/api/projects/{projectId}/business-objectives")).Body!.AsArray();
        Assert.Single(objectives[0]!["requirements"]!.AsArray());
        Assert.Empty(objectives[1]!["requirements"]!.AsArray());

        // Upstream objectives are never reported as "impacted" by an FR change.
        var frId = Id(generated!["functionalRequirements"]![0]);
        var impact = (await _ba.GetAsync($"/api/artifacts/{frId}/impact")).Body!;
        Assert.DoesNotContain(impact["otherDownstream"]!.AsArray().Concat(impact["approvedOrLaterDownstream"]!.AsArray()),
            i => i!["code"]!.GetValue<string>().StartsWith("BO-"));
    }

    [Fact]
    public async Task Requirements_can_be_linked_to_objectives_manually_but_only_to_objectives_of_the_same_project()
    {
        var (projectId, sourceId) = await ProjectAsync();
        var (_, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        var frId = Id(generated!["functionalRequirements"]![0]);
        var objectives = (await _ba.GetAsync($"/api/projects/{projectId}/business-objectives")).Body!.AsArray();

        var (s, linked) = await _ba.SendAsync(HttpMethod.Put, $"/api/artifacts/{frId}/business-objectives",
            new { businessObjectiveIds = new[] { Id(objectives[1]) } });
        Assert.Equal(200, s);
        Assert.Equal("BO-002", linked!.AsArray().Single()!["code"]!.GetValue<string>());

        var (otherProject, _) = await ProjectAsync("- Something else");
        var foreign = (await _ba.GetAsync($"/api/projects/{otherProject}/business-objectives")).Body!.AsArray()[0];
        Assert.Equal(400, (await _ba.SendAsync(HttpMethod.Put, $"/api/artifacts/{frId}/business-objectives",
            new { businessObjectiveIds = new[] { Id(foreign) } })).Status);

        Assert.Equal(403, (await _dev.SendAsync(HttpMethod.Put, $"/api/artifacts/{frId}/business-objectives",
            new { businessObjectiveIds = Array.Empty<string>() })).Status);
    }

    [Fact]
    public async Task Updating_project_objectives_adds_new_objectives_without_touching_existing_ones()
    {
        var (projectId, _) = await ProjectAsync("Reduce leave approval time");
        await _ba.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}",
            new { name = "Leave", objectives = "1. Reduce leave approval time\n2. Cut payroll errors" });

        var objectives = (await _ba.GetAsync($"/api/projects/{projectId}/business-objectives")).Body!.AsArray();
        Assert.Equal(new[] { "Reduce leave approval time", "Cut payroll errors" },
            objectives.Select(o => o!["title"]!.GetValue<string>()).ToArray());
    }

    // ---- §22 automatic, persistent impact notices ------------------------------------------------

    [Fact]
    public async Task Changing_an_approved_requirement_flags_downstream_artifacts_without_modifying_them()
    {
        var (projectId, sourceId) = await ProjectAsync();
        var (_, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        var frId = Id(generated!["functionalRequirements"]![0]);
        var (_, stories) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        var storyId = Id(stories![0]);
        var (_, criteria) = await _ba.PostAsync($"/api/artifacts/{storyId}/generate-acceptance-criteria");
        var (_, tasks) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-tasks");
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{storyId}/status", new { status = Approved })).Status);
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = Approved })).Status);

        // Editing an unapproved artifact raises nothing.
        Assert.Empty((await _ba.GetAsync($"/api/projects/{projectId}/impact-notices")).Body!.AsArray());

        var (es, edited) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "Submit leave with manager delegation", reason = "Delegation added" });
        Assert.Equal(200, es);
        Assert.Equal(UnderReview, edited!["status"]!.GetValue<int>());

        var notices = (await _ba.GetAsync($"/api/projects/{projectId}/impact-notices")).Body!.AsArray();
        var affected = notices.Select(n => n!["affectedArtifactId"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(storyId, affected);                        // directly derived
        Assert.Contains(Id(criteria![0]), affected);               // two hops: AC <- story <- FR
        Assert.Contains(Id(tasks![0]), affected);                  // implements the FR
        Assert.All(notices, n => Assert.Equal(2, n!["sourceVersion"]!.GetValue<int>()));
        Assert.Contains(notices, n => n!["path"]!.GetValue<string>().Contains("derived from") && n["path"]!.GetValue<string>().EndsWith("FR-001"));

        // Nothing downstream was modified - the approved story keeps its approval and version.
        var story = (await _ba.GetAsync($"/api/artifacts/{storyId}")).Body!;
        Assert.Equal(Approved, story["status"]!.GetValue<int>());
        Assert.Equal(1, story["currentVersion"]!.GetValue<int>());
        Assert.Equal(1, story["openImpactNoticeCount"]!.GetValue<int>());

        var dashboard = (await _ba.GetAsync($"/api/projects/{projectId}/dashboard")).Body!;
        Assert.Equal(notices.Count, dashboard["openImpactNoticeCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Notices_stay_open_until_acknowledged_and_a_later_change_supersedes_rather_than_duplicates()
    {
        var (projectId, sourceId) = await ProjectAsync();
        var (_, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        var frId = Id(generated!["functionalRequirements"]![0]);
        var (_, stories) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        var storyId = Id(stories![0]);

        await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = Approved });
        await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "Change one" });
        await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = Approved });
        await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "Change two" });

        var open = (await _ba.GetAsync($"/api/artifacts/{storyId}/impact-notices")).Body!.AsArray()
            .Where(n => n!["acknowledgedAt"] is null).ToList();
        var notice = Assert.Single(open);
        Assert.Equal(3, notice!["sourceVersion"]!.GetValue<int>());

        // A developer who owns downstream work can acknowledge, with a note; it's then closed but kept.
        var (ack, acknowledged) = await _dev.PostAsync($"/api/impact-notices/{Id(notice)}/acknowledge", new { note = "Story still valid" });
        Assert.Equal(200, ack);
        Assert.Equal("Story still valid", acknowledged!["acknowledgementNote"]!.GetValue<string>());
        Assert.Empty((await _ba.GetAsync($"/api/projects/{projectId}/impact-notices")).Body!.AsArray());
        Assert.Equal(2, (await _ba.GetAsync($"/api/projects/{projectId}/impact-notices?includeAcknowledged=true")).Body!.AsArray().Count);
    }

    [Fact]
    public async Task Impact_notices_are_not_visible_or_acknowledgeable_outside_the_project()
    {
        var (projectId, sourceId) = await ProjectAsync();
        var (_, generated) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        var frId = Id(generated!["functionalRequirements"]![0]);
        await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");
        await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}/status", new { status = Approved });
        await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frId}", new { title = "Changed" });
        var noticeId = Id((await _ba.GetAsync($"/api/projects/{projectId}/impact-notices")).Body!.AsArray()[0]);

        var outsider = await _host.RegisterAsync("BusinessAnalyst");
        Assert.Equal(403, (await outsider.GetAsync($"/api/projects/{projectId}/impact-notices")).Status);
        Assert.Equal(403, (await outsider.PostAsync($"/api/impact-notices/{noticeId}/acknowledge", new { note = "x" })).Status);
    }

    // ---- §25 Copilot grounding ------------------------------------------------------------------

    [Fact]
    public async Task Copilot_is_given_requirement_content_and_a_deterministic_impact_walk_for_named_codes()
    {
        var (projectId, sourceId) = await ProjectAsync();
        await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-user-stories");

        var (s, answer) = await _ba.PostAsync($"/api/projects/{projectId}/copilot/ask", new { question = "What is impacted if FR-001 changes?" });

        Assert.Equal(200, s);
        Assert.False(string.IsNullOrWhiteSpace(answer!["answer"]!.GetValue<string>()));
        var prompt = _host.Ai.UserPrompts.Last();
        Assert.Contains("PROJECT ARTIFACTS:", prompt);
        Assert.Contains("processing=Validate input and persist the request.", prompt, StringComparison.OrdinalIgnoreCase); // actual FR content, not just a count
        Assert.Contains("IMPACT ANALYSIS:", prompt);
        Assert.Contains("If FR-001 changes", prompt);
        Assert.Contains("US-001", prompt);
    }

    [Fact]
    public async Task Copilot_context_never_includes_another_projects_artifacts()
    {
        var (projectA, sourceA) = await ProjectAsync();
        await _ba.PostAsync($"/api/requirement-sources/{sourceA}/generate-requirements");
        var (_, projectB) = await _ba.PostAsync("/api/projects", new { name = "Other", description = "d", objectives = "Secret objective of project B" });

        await _ba.PostAsync($"/api/projects/{projectA}/copilot/ask", new { question = "List all objectives" });

        Assert.DoesNotContain("Secret objective of project B", _host.Ai.UserPrompts.Last());
        Assert.NotNull(projectB);
    }

    // ---- §26 RAG in generation ------------------------------------------------------------------

    [Fact]
    public async Task Uploaded_project_documents_are_retrieved_into_requirement_generation()
    {
        var (projectId, sourceId) = await ProjectAsync();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "Meeting notes: managers approve leave requests within two working days. Employees submit leave requests online."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "meeting-notes.txt");
        var upload = await _ba.Http.PostAsync($"/api/projects/{projectId}/documents/upload", form);
        Assert.Equal(201, (int)upload.StatusCode);

        Assert.Equal(200, (await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);

        Assert.Contains(_host.Ai.UserPrompts, p => p.Contains("Project document excerpts") && p.Contains("[meeting-notes.txt#0]"));

        // Re-index is idempotent when everything is already on the current embedding model.
        var (rs, reindex) = await _ba.PostAsync($"/api/projects/{projectId}/documents/reindex");
        Assert.Equal(200, rs);
        Assert.Equal(0, reindex!["reindexedChunks"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_requirement_citing_a_document_it_was_not_given_is_rejected()
    {
        var (_, sourceId) = await ProjectAsync();
        const string invented = """{"functionalRequirements":[{"title":"Submit","actor":"Employee","priority":"High","sourceReferences":["imaginary.pdf#9"]}],"nonFunctionalRequirements":[]}""";
        _host.Ai.RespondOnceWhenPromptContains("functionalRequirements", invented);
        _host.Ai.RespondOnceWhenPromptContains("functionalRequirements", invented);

        var (s, body) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, s);
        Assert.Contains(body!["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("imaginary.pdf#9"));
    }
}
