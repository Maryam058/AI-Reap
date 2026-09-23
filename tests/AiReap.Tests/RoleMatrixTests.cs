using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// GET /api/roles/matrix (RolesController) reflects the API's real [Capability]-tagged
// [Authorize(Roles = ...)] rules, replacing a hand-maintained static mirror on the frontend
// (web/src/pages/RolesPage.tsx) that could - and once did - silently drift from what the API
// actually enforces (the §38 project-membership endpoints were never added to the old static
// table). This asserts the matrix reports the real, current rules rather than merely "returns 200".
public class RoleMatrixTests : IAsyncLifetime
{
    private readonly TestHost _host = new();
    private ApiUser _dev = null!;

    public async Task InitializeAsync()
    {
        _dev = await _host.RegisterAsync("Developer");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static JsonArray Roles(JsonArray groups, string group, string label) =>
        groups.Single(g => g!["group"]!.GetValue<string>() == group)!["items"]!.AsArray()
            .Single(i => i!["label"]!.GetValue<string>() == label)!["roles"]!.AsArray();

    [Fact]
    public async Task Matrix_reflects_the_real_authorize_attributes_not_a_stale_hand_written_copy()
    {
        var (status, body) = await _dev.GetAsync("/api/roles/matrix");
        Assert.Equal(200, status);
        var groups = body!.AsArray();

        // Administrator-only capability really only lists Administrator.
        var adminRoles = Roles(groups, "Administer", "Create users, assign roles, activate and deactivate accounts").Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Administrator" }, adminRoles);

        // A bare [Authorize] action (no Roles) expands to every defined role.
        var viewRoles = Roles(groups, "View", "Projects, requirements, design, tasks, tests, dashboard and traceability").Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Administrator", "BusinessAnalyst", "Developer", "QA", "Reviewer" }, viewRoles);

        // §38 project-membership endpoints - added to the API after the old static RolesPage
        // table was last hand-updated, and never added there. A reflection-based matrix can't
        // have that gap: it's here because ProjectsController.AddMember is actually tagged.
        var memberRoles = Roles(groups, "Author", "Add or remove project members").Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Administrator", "BusinessAnalyst" }, memberRoles);

        // Review-gated capability lists exactly the three reviewer-capable roles.
        var reviewRoles = Roles(groups, "Review", "Approve, reject and update artifact status").Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Administrator", "BusinessAnalyst", "Reviewer" }, reviewRoles);
    }
}
