using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §38 DoD — "auth/authz across multiple projects." ProjectMember + IProjectAccessService sit on
// top of the pre-existing role gates (WriterRoles/ReviewerRoles etc.): a user with a perfectly
// valid role can still be refused a specific project's data if they're not a member of it.
public class ProjectAccessControlTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _creator = null!, _outsider = null!, _admin = null!;

    public async Task InitializeAsync()
    {
        _creator = await _host.RegisterAsync("BusinessAnalyst");
        _outsider = await _host.RegisterAsync("BusinessAnalyst");
        _admin = await _host.RegisterAsync("Administrator");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    [Fact]
    public async Task Creator_is_automatically_a_member_and_can_read_their_own_new_project()
    {
        var (status, project) = await _creator.PostAsync("/api/projects", new { name = "Mine" });
        Assert.Equal(201, status);
        var id = Id(project);

        Assert.Equal(200, (await _creator.GetAsync($"/api/projects/{id}")).Status);
        var members = (await _creator.GetAsync($"/api/projects/{id}/members")).Body!.AsArray();
        Assert.Single(members);
    }

    [Fact]
    public async Task A_non_member_with_a_valid_role_is_refused_on_both_direct_and_indirect_routes()
    {
        var (ps, project) = await _creator.PostAsync("/api/projects", new { name = "Private" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        var (ss, source) = await _creator.PostAsync($"/api/projects/{projectId}/requirement-sources",
            new { sourceType = 1, rawText = "Employees submit leave requests; managers approve." });
        Assert.InRange(ss, 200, 201);
        var sourceId = Id(source);
        var (gs, generated) = await _creator.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, gs);
        var frId = Id(generated!["functionalRequirements"]![0]);

        // Direct-projectId route - covered by the global ProjectMembershipFilter.
        Assert.Equal(403, (await _outsider.GetAsync($"/api/projects/{projectId}/requirement-sources")).Status);
        // Indirect route resolved via the artifact's own ProjectId - covered by ArtifactService's guard.
        Assert.Equal(403, (await _outsider.GetAsync($"/api/artifacts/{frId}")).Status);
        // Indirect route resolved via the requirement source's own ProjectId.
        Assert.Equal(403, (await _outsider.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);

        // Not in the caller's own project list either - GetAllAsync filters by membership.
        var list = (await _outsider.GetAsync("/api/projects")).Body!.AsArray();
        Assert.DoesNotContain(list, x => Id(x) == projectId);

        // Administrator bypasses membership entirely.
        Assert.Equal(200, (await _admin.GetAsync($"/api/projects/{projectId}/requirement-sources")).Status);
    }

    [Fact]
    public async Task Adding_a_member_grants_access_removing_revokes_it_and_only_ba_or_admin_can_manage_membership()
    {
        var (ps, project) = await _creator.PostAsync("/api/projects", new { name = "Shared" });
        Assert.Equal(201, ps);
        var projectId = Id(project);
        var dev = await _host.RegisterAsync("Developer");

        // Developer role can't manage membership (BA/Admin only).
        Assert.Equal(403, (await dev.PostAsync($"/api/projects/{projectId}/members", new { email = dev.Email })).Status);
        // Not yet a member.
        Assert.Equal(403, (await dev.GetAsync($"/api/projects/{projectId}")).Status);

        // Unknown email is rejected.
        Assert.Equal(404, (await _creator.PostAsync($"/api/projects/{projectId}/members", new { email = "nobody@nowhere.test" })).Status);

        var (addStatus, member) = await _creator.PostAsync($"/api/projects/{projectId}/members", new { email = dev.Email });
        Assert.InRange(addStatus, 200, 201);
        Assert.Equal(200, (await dev.GetAsync($"/api/projects/{projectId}")).Status);

        // Adding the same member again is idempotent, not a duplicate.
        Assert.InRange((await _creator.PostAsync($"/api/projects/{projectId}/members", new { email = dev.Email })).Status, 200, 201);
        Assert.Equal(2, (await _creator.GetAsync($"/api/projects/{projectId}/members")).Body!.AsArray().Count); // creator + dev

        // Removing revokes access again.
        var devUserId = member!["userId"]!.GetValue<string>();
        Assert.Equal(204, (await _creator.SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}/members/{devUserId}")).Status);
        Assert.Equal(403, (await dev.GetAsync($"/api/projects/{projectId}")).Status);
    }
}
