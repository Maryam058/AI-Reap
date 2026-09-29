using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// §5/§29/REAP-023 — stakeholder management is a Business Analyst responsibility. Developer, QA and
// Reviewer members can read the stakeholder list but every mutation is refused server-side (403),
// not just hidden in the UI. Administrator keeps its app-wide override (it's in every role gate and
// bypasses membership - see IProjectAccessService). Project membership still applies to BAs.
public class StakeholderPermissionsTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _outsiderBa = null!, _dev = null!, _qa = null!, _reviewer = null!, _admin = null!;
    private string _projectId = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _outsiderBa = await _host.RegisterAsync("BusinessAnalyst");
        _dev = await _host.RegisterAsync("Developer");
        _qa = await _host.RegisterAsync("QA");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _admin = await _host.RegisterAsync("Administrator");

        var (ps, project) = await _ba.PostAsync("/api/projects", new { name = "Stakeholders" });
        Assert.Equal(201, ps);
        _projectId = Id(project);
        foreach (var member in new[] { _dev, _qa, _reviewer })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, _projectId, member)).Status, 200, 201);
        }
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private string Stakeholders => $"/api/projects/{_projectId}/stakeholders";

    private async Task<string> AddAsync(ApiUser user, string name)
    {
        var (status, body) = await user.PostAsync(Stakeholders, new { name, roleInProject = "Developer", contactInfo = "x@test.io" });
        Assert.Equal(201, status);
        return Id(body);
    }

    [Fact]
    public async Task Business_analyst_can_add_edit_and_remove_stakeholders()
    {
        var id = await AddAsync(_ba, "Ali");

        var (us, updated) = await _ba.SendAsync(HttpMethod.Put, $"{Stakeholders}/{id}", new { name = "Ali Khan", roleInProject = "QA", contactInfo = "ali@test.io" });
        Assert.Equal(200, us);
        Assert.Equal("Ali Khan", updated!["name"]!.GetValue<string>());
        Assert.Equal("QA", updated["roleInProject"]!.GetValue<string>());

        Assert.Equal(204, (await _ba.SendAsync(HttpMethod.Delete, $"{Stakeholders}/{id}")).Status);
        Assert.Empty((await _ba.GetAsync(Stakeholders)).Body!.AsArray());
    }

    [Fact]
    public async Task Developer_qa_and_reviewer_can_view_but_every_mutation_is_forbidden()
    {
        var id = await AddAsync(_ba, "Sam");

        foreach (var user in new[] { _dev, _qa, _reviewer })
        {
            var (ls, list) = await user.GetAsync(Stakeholders);
            Assert.Equal(200, ls);
            Assert.Single(list!.AsArray());

            Assert.Equal(403, (await user.PostAsync(Stakeholders, new { name = "X" })).Status);
            Assert.Equal(403, (await user.SendAsync(HttpMethod.Put, $"{Stakeholders}/{id}", new { name = "X" })).Status);
            Assert.Equal(403, (await user.SendAsync(HttpMethod.Delete, $"{Stakeholders}/{id}")).Status);
            Assert.Equal(403, (await user.GetAsync($"{Stakeholders}/available-users")).Status);
        }

        // Nothing changed.
        var remaining = (await _ba.GetAsync(Stakeholders)).Body!.AsArray();
        Assert.Equal("Sam", Assert.Single(remaining)!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_business_analyst_who_is_not_a_project_member_is_refused()
    {
        var id = await AddAsync(_ba, "Sam");

        Assert.Equal(403, (await _outsiderBa.GetAsync(Stakeholders)).Status);
        Assert.Equal(403, (await _outsiderBa.PostAsync(Stakeholders, new { name = "X" })).Status);
        Assert.Equal(403, (await _outsiderBa.SendAsync(HttpMethod.Put, $"{Stakeholders}/{id}", new { name = "X" })).Status);
        Assert.Equal(403, (await _outsiderBa.SendAsync(HttpMethod.Delete, $"{Stakeholders}/{id}")).Status);
        Assert.Equal(403, (await _outsiderBa.GetAsync($"{Stakeholders}/available-users")).Status);
    }

    [Fact]
    public async Task Administrator_keeps_its_override_for_stakeholder_maintenance()
    {
        var id = await AddAsync(_admin, "Admin-added");
        Assert.Equal(200, (await _admin.SendAsync(HttpMethod.Put, $"{Stakeholders}/{id}", new { name = "Renamed" })).Status);
        Assert.Equal(204, (await _admin.SendAsync(HttpMethod.Delete, $"{Stakeholders}/{id}")).Status);
        Assert.Equal(200, (await _admin.GetAsync($"{Stakeholders}/available-users")).Status);
    }

    [Fact]
    public async Task Available_users_lookup_returns_only_active_users_with_minimal_fields_and_users_api_stays_admin_only()
    {
        var path = $"{Stakeholders}/available-users";
        Assert.Equal(403, (await _ba.GetAsync("/api/users")).Status);

        var (status, candidates) = await _ba.GetAsync(path);
        Assert.Equal(200, status);
        var dev = candidates!.AsArray().Single(u => u!["email"]!.GetValue<string>() == _dev.Email)!;
        Assert.Equal("Developer", dev["role"]!.GetValue<string>());
        Assert.True(dev["isActive"]!.GetValue<bool>());
        Assert.Equal(new[] { "displayName", "email", "id", "isActive", "role" }, dev.AsObject().Select(p => p.Key).OrderBy(k => k));

        var inactive = await _host.RegisterAsync("QA");
        var inactiveId = (await _admin.GetAsync("/api/users")).Body!.AsArray()
            .Single(u => u!["email"]!.GetValue<string>() == inactive.Email)!["id"]!.GetValue<string>();
        Assert.Equal(200, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{inactiveId}/status", new { isActive = false })).Status);
        Assert.DoesNotContain((await _ba.GetAsync(path)).Body!.AsArray(), u => u!["email"]!.GetValue<string>() == inactive.Email);
    }
}
