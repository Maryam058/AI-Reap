using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiReap.Domain.Common;
using Xunit;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// REAP-046 — manual User Story dependencies. "US-B depends on US-A" is stored as
// US-B -DependsOn-> US-A through POST/DELETE /api/artifacts/{id}/dependencies. Only a Business
// Analyst or Administrator may change them; they are relationships, not content, so no version,
// status or approval changes; existing impact analysis follows them and the traceability matrix
// ignores them.
public class StoryDependencyTests : IAsyncLifetime
{
    private const int FunctionalRequirement = 0, UserStory = 3, DependsOn = 3, Approved = 3;

    private readonly TestHost _host = new();
    private Actor _ba = null!, _admin = null!, _dev = null!, _qa = null!, _reviewer = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.ActorAsync(Roles.BusinessAnalyst);
        _admin = await _host.AdminAsync();
        _dev = await _host.ActorAsync(Roles.Developer);
        _qa = await _host.ActorAsync(Roles.QA);
        _reviewer = await _host.ActorAsync(Roles.Reviewer);
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<string> ProjectAsync()
    {
        var (ps, project) = await _ba.Api.PostAsync("/api/projects", new { name = "Leave", description = "d" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        foreach (var member in new[] { _admin, _dev, _qa, _reviewer })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba.Api, projectId, member.Api)).Status, 200, 201);
        }

        return projectId;
    }

    // Seeded directly: the stub AI produces a single story per source, these tests need several.
    private async Task<string> SeedAsync(string projectId, string code, int type = UserStory, int status = 0)
    {
        var id = Guid.NewGuid();
        await _host.ExecuteAsync($"""
            INSERT INTO Artifacts (Id, ProjectId, ArtifactType, Code, Title, Status, Origin, DataJson, CurrentVersion, CreatedByUserId, CreatedAt, UpdatedAt)
            VALUES ('{id}', '{projectId}', {type}, '{code}', 'Story {code}', {status}, 1, '{"{}"}', 1, '{_ba.Id}', GETUTCDATE(), GETUTCDATE())
            """);
        return id.ToString();
    }

    private static Task<(int Status, JsonNode? Body)> AddAsync(Actor user, string storyId, string dependsOnId) =>
        user.Api.PostAsync($"/api/artifacts/{storyId}/dependencies", new { dependsOnArtifactId = dependsOnId });

    private static Task<(int Status, JsonNode? Body)> RemoveAsync(Actor user, string storyId, string targetId) =>
        user.Api.SendAsync(HttpMethod.Delete, $"/api/artifacts/{storyId}/dependencies/{targetId}");

    private async Task<List<JsonNode>> RelationshipsAsync(string id) =>
        (await _ba.Api.GetAsync($"/api/artifacts/{id}/relationships")).Body!.AsArray().Select(r => r!).ToList();

    private Task<int> DependsOnCountAsync(string source, string target) =>
        _host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactRelationships WHERE SourceArtifactId = '{source}' AND TargetArtifactId = '{target}' AND RelationshipType = {DependsOn}");

    private static string Str(JsonNode? n, string key) => n![key]!.GetValue<string>();

    // ---- authorization ---------------------------------------------------------------------

    [Fact]
    public async Task Business_analyst_and_administrator_can_add_and_remove_dependencies()
    {
        var projectId = await ProjectAsync();
        var a = await SeedAsync(projectId, "US-901");
        var b = await SeedAsync(projectId, "US-902");
        var c = await SeedAsync(projectId, "US-903");

        foreach (var (user, source) in new[] { (_ba, b), (_admin, c) })
        {
            var (added, body) = await AddAsync(user, source, a);
            Assert.Equal(201, added);
            Assert.Equal(a, Str(body, "relatedArtifactId"));
            Assert.Equal(DependsOn, body!["relationshipType"]!.GetValue<int>());
            Assert.True(body["isOutgoing"]!.GetValue<bool>());
        }

        Assert.Equal(204, (await RemoveAsync(_admin, b, a)).Status);
        Assert.Equal(204, (await RemoveAsync(_ba, c, a)).Status);
        Assert.Equal(0, await DependsOnCountAsync(b, a));
        Assert.Equal(0, await DependsOnCountAsync(c, a));
    }

    [Fact]
    public async Task Developer_qa_and_reviewer_get_403_and_anonymous_callers_get_401()
    {
        var projectId = await ProjectAsync();
        var a = await SeedAsync(projectId, "US-901", status: Approved);
        var b = await SeedAsync(projectId, "US-902", status: Approved);

        foreach (var user in new[] { _dev, _qa, _reviewer })
        {
            Assert.Equal(403, (await AddAsync(user, b, a)).Status);
        }
        Assert.Equal(0, await DependsOnCountAsync(b, a));

        Assert.Equal(201, (await AddAsync(_ba, b, a)).Status);
        foreach (var user in new[] { _dev, _qa, _reviewer })
        {
            Assert.Equal(403, (await RemoveAsync(user, b, a)).Status);
        }
        Assert.Equal(1, await DependsOnCountAsync(b, a));

        var anonymous = _host.Factory.CreateClient();
        Assert.Equal(401, (int)(await anonymous.PostAsJsonAsync($"/api/artifacts/{b}/dependencies", new { dependsOnArtifactId = a })).StatusCode);
        Assert.Equal(401, (int)(await anonymous.DeleteAsync($"/api/artifacts/{b}/dependencies/{a}")).StatusCode);
        Assert.Equal(1, await DependsOnCountAsync(b, a));
    }

    [Fact]
    public async Task A_business_analyst_outside_the_project_cannot_add_a_dependency()
    {
        var projectId = await ProjectAsync();
        var a = await SeedAsync(projectId, "US-901");
        var b = await SeedAsync(projectId, "US-902");
        var outsider = await _host.ActorAsync(Roles.BusinessAnalyst);

        Assert.Equal(403, (await AddAsync(outsider, b, a)).Status);
        Assert.Equal(0, await DependsOnCountAsync(b, a));
    }

    [Fact]
    public async Task The_roles_matrix_lists_only_administrator_and_business_analyst_for_story_dependencies()
    {
        var groups = (await _dev.Api.GetAsync("/api/roles/matrix")).Body!.AsArray();
        var roles = groups.Single(g => Str(g, "group") == "Author")!["items"]!.AsArray()
            .Single(i => Str(i, "label") == "Link user story dependencies")!["roles"]!.AsArray()
            .Select(r => r!.GetValue<string>());
        Assert.Equal(new[] { "Administrator", "BusinessAnalyst" }, roles);
    }

    // ---- validation ------------------------------------------------------------------------

    [Fact]
    public async Task Source_and_target_must_be_user_stories_of_the_same_project_and_not_the_same_story()
    {
        var projectId = await ProjectAsync();
        var story = await SeedAsync(projectId, "US-901");
        var fr = await SeedAsync(projectId, "FR-901", FunctionalRequirement);
        var otherProject = await ProjectAsync();
        var foreignStory = await SeedAsync(otherProject, "US-951");

        Assert.Equal(400, (await AddAsync(_ba, fr, story)).Status);           // source not a story
        Assert.Equal(400, (await AddAsync(_ba, story, fr)).Status);           // target not a story
        Assert.Equal(400, (await AddAsync(_ba, story, foreignStory)).Status); // other project
        Assert.Equal(400, (await AddAsync(_ba, story, story)).Status);        // itself
        Assert.Equal(400, (await AddAsync(_ba, story, Guid.NewGuid().ToString())).Status); // no such target
        Assert.Equal(404, (await AddAsync(_ba, Guid.NewGuid().ToString(), story)).Status); // no such source

        Assert.Equal(0, await _host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactRelationships WHERE RelationshipType = {DependsOn}"));
    }

    [Fact]
    public async Task A_duplicate_dependency_is_rejected_with_409()
    {
        var projectId = await ProjectAsync();
        var a = await SeedAsync(projectId, "US-901");
        var b = await SeedAsync(projectId, "US-902");

        Assert.Equal(201, (await AddAsync(_ba, b, a)).Status);
        var (status, body) = await AddAsync(_admin, b, a);

        Assert.Equal(409, status);
        Assert.Equal("conflict", Str(body, "code"));
        Assert.Equal(1, await DependsOnCountAsync(b, a));
    }

    [Fact]
    public async Task Direct_and_longer_dependency_cycles_are_rejected()
    {
        var projectId = await ProjectAsync();
        var us1 = await SeedAsync(projectId, "US-001");
        var us2 = await SeedAsync(projectId, "US-002");
        var us3 = await SeedAsync(projectId, "US-003");

        // US-001 -> US-002 forbids US-002 -> US-001.
        Assert.Equal(201, (await AddAsync(_ba, us1, us2)).Status);
        var (direct, body) = await AddAsync(_ba, us2, us1);
        Assert.Equal(409, direct);
        Assert.Contains("cycle", Str(body, "detail"));

        // US-001 -> US-002 -> US-003 forbids US-003 -> US-001.
        Assert.Equal(201, (await AddAsync(_ba, us2, us3)).Status);
        Assert.Equal(409, (await AddAsync(_ba, us3, us1)).Status);

        Assert.Equal(0, await DependsOnCountAsync(us2, us1));
        Assert.Equal(0, await DependsOnCountAsync(us3, us1));

        // A diamond is not a cycle: US-003 may also depend on something US-001 depends on.
        var us4 = await SeedAsync(projectId, "US-004");
        Assert.Equal(201, (await AddAsync(_ba, us3, us4)).Status);
        Assert.Equal(201, (await AddAsync(_ba, us1, us4)).Status);
    }

    [Fact]
    public async Task Removing_a_missing_dependency_or_a_non_dependency_relationship_returns_404()
    {
        var projectId = await ProjectAsync();
        var a = await SeedAsync(projectId, "US-901");
        var b = await SeedAsync(projectId, "US-902");

        Assert.Equal(404, (await RemoveAsync(_ba, b, a)).Status);
        Assert.Equal(404, (await RemoveAsync(_ba, Guid.NewGuid().ToString(), a)).Status);

        // Only the DependsOn link goes; another relationship between the same pair is not a dependency.
        await _host.ExecuteAsync($"""
            INSERT INTO ArtifactRelationships (Id, SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt)
            VALUES (NEWID(), '{b}', '{a}', 5, GETUTCDATE())
            """);
        Assert.Equal(404, (await RemoveAsync(_ba, b, a)).Status);

        // The reverse direction is not the stored dependency either.
        Assert.Equal(201, (await AddAsync(_ba, b, a)).Status);
        Assert.Equal(404, (await RemoveAsync(_ba, a, b)).Status);
        Assert.Equal(204, (await RemoveAsync(_ba, b, a)).Status);
        Assert.Equal(1, await _host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactRelationships WHERE SourceArtifactId = '{b}' AND TargetArtifactId = '{a}'"));
    }

    // ---- persistence -----------------------------------------------------------------------

    [Fact]
    public async Task A_dependency_is_read_back_in_the_right_direction_and_removal_deletes_it()
    {
        var projectId = await ProjectAsync();
        var a = await SeedAsync(projectId, "US-901");
        var b = await SeedAsync(projectId, "US-902");

        Assert.Equal(201, (await AddAsync(_ba, b, a)).Status);

        // US-902 -DependsOn-> US-901: outgoing from the dependent story, incoming on its dependency.
        var fromB = Assert.Single(await RelationshipsAsync(b));
        Assert.Equal(a, Str(fromB, "relatedArtifactId"));
        Assert.Equal("US-901", Str(fromB, "relatedArtifactCode"));
        Assert.Equal(DependsOn, fromB["relationshipType"]!.GetValue<int>());
        Assert.True(fromB["isOutgoing"]!.GetValue<bool>());

        var onA = Assert.Single(await RelationshipsAsync(a));
        Assert.Equal(b, Str(onA, "relatedArtifactId"));
        Assert.False(onA["isOutgoing"]!.GetValue<bool>());

        Assert.Equal(1, await DependsOnCountAsync(b, a));
        Assert.Equal(0, await DependsOnCountAsync(a, b));

        Assert.Equal(204, (await RemoveAsync(_ba, b, a)).Status);
        Assert.Empty(await RelationshipsAsync(b));
        Assert.Empty(await RelationshipsAsync(a));
    }

    // ---- regression ------------------------------------------------------------------------

    [Fact]
    public async Task Linking_an_approved_story_creates_no_version_and_leaves_status_and_approved_version_alone()
    {
        var projectId = await ProjectAsync();
        var (_, source) = await _ba.Api.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve them within two days." });
        Assert.Equal(200, (await _ba.Api.PostAsync($"/api/requirement-sources/{Id(source)}/generate-requirements")).Status);
        Assert.Equal(200, (await _ba.Api.PostAsync($"/api/requirement-sources/{Id(source)}/generate-user-stories")).Status);
        var story = Id((await _ba.Api.GetAsync($"/api/projects/{projectId}/artifacts?type=UserStory")).Body!.AsArray().Single());
        Assert.Equal(200, (await _reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{story}/status", new { status = Approved, comment = "ok" })).Status);
        var other = await SeedAsync(projectId, "US-950", status: Approved);

        var before = (await _ba.Api.GetAsync($"/api/artifacts/{story}")).Body!;
        var versionsBefore = (await _ba.Api.GetAsync($"/api/artifacts/{story}/versions")).Body!.AsArray().Count;
        var matrixBefore = (await _ba.Api.GetAsync($"/api/projects/{projectId}/traceability-matrix")).Body!.ToJsonString();

        // As the dependent story, and as the story depended on.
        Assert.Equal(201, (await AddAsync(_ba, story, other)).Status);
        Assert.Equal(204, (await RemoveAsync(_ba, story, other)).Status);
        Assert.Equal(201, (await AddAsync(_admin, other, story)).Status);

        var after = (await _ba.Api.GetAsync($"/api/artifacts/{story}")).Body!;
        foreach (var field in new[] { "status", "currentVersion", "approvedVersion", "title", "data", "updatedAt" })
        {
            Assert.Equal(before[field]?.ToJsonString(), after[field]?.ToJsonString());
        }
        Assert.Equal(Approved, after["status"]!.GetValue<int>());
        Assert.Equal(versionsBefore, (await _ba.Api.GetAsync($"/api/artifacts/{story}/versions")).Body!.AsArray().Count);
        Assert.Equal(0, await _host.ScalarAsync($"SELECT COUNT(*) FROM ArtifactImpactNotices WHERE AffectedArtifactId IN ('{story}', '{other}')"));

        // The traceability matrix does not carry DependsOn.
        Assert.Equal(matrixBefore, (await _ba.Api.GetAsync($"/api/projects/{projectId}/traceability-matrix")).Body!.ToJsonString());
    }

    [Fact]
    public async Task Impact_analysis_of_a_story_reports_the_stories_that_depend_on_it()
    {
        var projectId = await ProjectAsync();
        var us1 = await SeedAsync(projectId, "US-001", status: Approved);
        var us2 = await SeedAsync(projectId, "US-002");

        Assert.Equal(201, (await AddAsync(_ba, us2, us1)).Status);

        var impact = (await _ba.Api.GetAsync($"/api/artifacts/{us1}/impact")).Body!;
        var impacted = Assert.Single(impact["otherDownstream"]!.AsArray())!;
        Assert.Equal(us2, Str(impacted, "id"));
        Assert.Equal("DependsOn", Str(impacted, "relationshipType"));
        Assert.Equal("US-002 depends on US-001", Str(impacted, "path"));

        // Upstream is never "impacted": US-002's own analysis doesn't list US-001.
        var reverse = (await _ba.Api.GetAsync($"/api/artifacts/{us2}/impact")).Body!;
        Assert.Empty(reverse["otherDownstream"]!.AsArray());
        Assert.Empty(reverse["approvedOrLaterDownstream"]!.AsArray());
    }
}
