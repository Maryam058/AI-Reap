using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §28 - SDLC phase completion is derived from the project's real work, not its lifecycle Status.
public class SdlcProgressTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<JsonNode> PhaseAsync(string projectId, string key)
    {
        var (status, dashboard) = await _ba.GetAsync($"/api/projects/{projectId}/dashboard");
        Assert.Equal(200, status);
        return dashboard!["phases"]!.AsArray().Single(p => p!["key"]!.GetValue<string>() == key)!;
    }

    private static bool Check(JsonNode phase, string label) =>
        phase["checks"]!.AsArray().Single(c => c!["label"]!.GetValue<string>() == label)!["passed"]!.GetValue<bool>();

    [Fact]
    public async Task Phases_complete_from_evidence_and_the_status_cannot_skip_it()
    {
        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Evidence", description = "d", objectives = "Cut leave admin time" });
        Assert.Equal(201, ps);
        var projectId = Id(project);

        var (_, dashboard) = await _ba.GetAsync($"/api/projects/{projectId}/dashboard");
        var keys = dashboard!["phases"]!.AsArray().Select(p => p!["key"]!.GetValue<string>()).ToArray();
        Assert.Equal(["gathering", "analysis", "validation", "design", "development", "testing", "traceability"], keys);
        Assert.All(dashboard["phases"]!.AsArray(), p => Assert.False(p!["complete"]!.GetValue<bool>()));

        var gathering = await PhaseAsync(projectId, "gathering");
        Assert.True(Check(gathering, "Business objectives recorded"));
        Assert.False(Check(gathering, "Raw requirements captured"));
        Assert.False(Check(gathering, "Stakeholders identified"));

        // The status can't be moved past missing evidence (Draft needs a business problem and scope),
        // and trying does not mark any phase complete.
        var (ss, _) = await _ba.SendAsync(HttpMethod.Patch, $"/api/projects/{projectId}/status", new { status = 1 });
        Assert.Equal(409, ss);
        Assert.False((await PhaseAsync(projectId, "gathering"))["complete"]!.GetValue<bool>());

        var (srcStatus, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(srcStatus, 200, 201);
        var (shStatus, _) = await _ba.PostAsync($"/api/projects/{projectId}/stakeholders", new { name = "HR Lead", roleInProject = "Sponsor" });
        Assert.InRange(shStatus, 200, 201);
        Assert.True((await PhaseAsync(projectId, "gathering"))["complete"]!.GetValue<bool>());

        var (gs, generated) = await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements");
        Assert.Equal(200, gs);
        Assert.False(Check(await PhaseAsync(projectId, "analysis"), "Functional requirements approved"));

        foreach (var fr in generated!["functionalRequirements"]!.AsArray())
        {
            var (approve, _) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{Id(fr)}/status", new { status = 3 });
            Assert.Equal(200, approve);
        }
        Assert.True(Check(await PhaseAsync(projectId, "analysis"), "Functional requirements approved"));
    }

    [Fact]
    public async Task Rejected_artifacts_do_not_block_a_phase_and_conflicts_resolve_when_one_side_is_rejected()
    {
        var (_, project) = await _ba.PostAsync("/api/projects", new { name = "Rejections", description = "d" });
        var projectId = Id(project);
        var (_, source) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        // A second source with the same text: generating twice from one source no longer creates
        // duplicate requirements (ArtifactFingerprints), but two sources may legitimately overlap.
        var (_, otherSource) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        var (_, first) = await _ba.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements");
        var (_, second) = await _ba.PostAsync($"/api/requirement-sources/{Id(otherSource)}/generate-requirements");
        var frA = Id(first!["functionalRequirements"]![0]);
        var frB = Id(second!["functionalRequirements"]![0]);

        // ConflictsWith = 4 (Domain/Enums/RelationshipType.cs).
        await _host.ExecuteAsync($"""
            INSERT INTO ArtifactRelationships (Id, SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt)
            VALUES (NEWID(), '{frA}', '{frB}', 4, GETUTCDATE())
            """);
        Assert.False(Check(await PhaseAsync(projectId, "validation"), "Conflicts resolved"));

        var (reject, _) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{frB}/status", new { status = 4, comment = "Duplicate" });
        Assert.Equal(200, reject);
        Assert.True(Check(await PhaseAsync(projectId, "validation"), "Conflicts resolved"));

        foreach (var fr in first["functionalRequirements"]!.AsArray().Concat(second["functionalRequirements"]!.AsArray()))
        {
            if (Id(fr) != frB)
            {
                await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{Id(fr)}/status", new { status = 3 });
            }
        }
        Assert.True(Check(await PhaseAsync(projectId, "analysis"), "Functional requirements approved"));
    }

    [Fact]
    public async Task Progress_list_covers_only_projects_the_caller_can_see()
    {
        var (_, mine) = await _ba.PostAsync("/api/projects", new { name = "Mine", description = "d" });
        var other = await _host.RegisterAsync("BusinessAnalyst");
        var (_, theirs) = await other.PostAsync("/api/projects", new { name = "Theirs", description = "d" });

        var (status, list) = await _ba.GetAsync("/api/projects/sdlc-progress");
        Assert.Equal(200, status);
        var ids = list!.AsArray().Select(p => p!["projectId"]!.GetValue<string>()).ToList();
        Assert.Contains(Id(mine), ids);
        Assert.DoesNotContain(Id(theirs), ids);
        Assert.Equal(7, list.AsArray().Single(p => p!["projectId"]!.GetValue<string>() == Id(mine))!["phases"]!.AsArray().Count);
    }
}
