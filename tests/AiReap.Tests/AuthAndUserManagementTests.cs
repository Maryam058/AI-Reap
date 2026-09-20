using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// Authentication + role management, enforced server-side. Everything here goes through the
// public HTTP API exactly as the React app does, so no UI trust is involved.
public class AuthAndUserManagementTests : IAsyncLifetime
{
    private static readonly string[] AllRoles = { "Administrator", "BusinessAnalyst", "Developer", "QA", "Reviewer" };
    private static readonly string[] PrivilegedNames = { "Administrator", "BusinessAnalyst", "Developer", "QA", "Reviewer", "Manager", "admin", "ADMINISTRATOR" };

    private readonly TestHost _host = new();
    private ApiUser _admin = null!;

    public async Task InitializeAsync() => _admin = await _host.LoginAsync(TestHost.AdminEmail, TestHost.AdminPassword);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private async Task<(string Id, ApiUser Client, string Email)> NewUserAsync(string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@test.io";
        var (s, body) = await _admin.PostAsync("/api/users", new { email, password = TestHost.UserPassword, displayName = role, role });
        Assert.Equal(201, s);
        return (Id(body), await _host.LoginAsync(email, TestHost.UserPassword), email);
    }

    // ---- Public sign-up -----------------------------------------------------------------

    [Fact]
    public async Task Public_signup_ignores_any_requested_role_and_grants_none()
    {
        var anon = new ApiUser("anon", _host.Factory.CreateClient());
        foreach (var requested in PrivilegedNames)
        {
            var email = $"{Guid.NewGuid():N}@signup.test";
            var (s, body) = await anon.PostAsync("/api/auth/register",
                new { email, password = TestHost.UserPassword, displayName = "Mallory", role = requested });
            Assert.Equal(200, s);
            Assert.Empty(body!["roles"]!.AsArray());

            var signedUp = await _host.LoginAsync(email, TestHost.UserPassword);
            var (ls, login) = (0, (JsonNode?)null);
            (ls, login) = await new ApiUser("a", _host.Factory.CreateClient()).PostAsync("/api/auth/login", new { email, password = TestHost.UserPassword });
            Assert.Equal(200, ls);
            Assert.Empty(login!["roles"]!.AsArray());

            // ...and the server refuses everything, including reads, and every admin function
            Assert.Equal(403, (await signedUp.GetAsync("/api/projects")).Status);
            Assert.Equal(403, (await signedUp.PostAsync("/api/projects", new { name = "x" })).Status);
            Assert.Equal(403, (await signedUp.GetAsync("/api/users")).Status);
            Assert.Equal(403, (await signedUp.PostAsync("/api/users",
                new { email = $"{Guid.NewGuid():N}@x.test", password = TestHost.UserPassword, displayName = "x", role = "Administrator" })).Status);
            Assert.Equal(403, (await signedUp.PostAsync("/api/agent-runs", new { requirementSourceId = Guid.NewGuid() })).Status);
        }

        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id = ur.RoleId WHERE r.Name = 'Administrator'"));
    }

    [Fact]
    public async Task Signup_validation_and_duplicate_email_are_rejected()
    {
        var anon = new ApiUser("anon", _host.Factory.CreateClient());
        Assert.Equal(400, (await anon.PostAsync("/api/auth/register", new { email = "not-an-email", password = TestHost.UserPassword, displayName = "x" })).Status);
        Assert.Equal(400, (await anon.PostAsync("/api/auth/register", new { email = "a@b.test", password = "short", displayName = "x" })).Status);
        Assert.Equal(400, (await anon.PostAsync("/api/auth/register", new { email = TestHost.AdminEmail, password = TestHost.UserPassword, displayName = "dup" })).Status);
    }

    // ---- Bootstrap administrator ---------------------------------------------------------

    [Fact]
    public async Task Bootstrap_admin_exists_can_sign_in_and_is_created_only_once()
    {
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers WHERE Email = '" + TestHost.AdminEmail + "'"));
        var (s, users) = await _admin.GetAsync("/api/users");
        Assert.Equal(200, s);
        Assert.Contains(users!.AsArray(), u => u!["role"]!.GetValue<string>() == "Administrator");

        // restart with different bootstrap credentials: an administrator exists, so nothing is added or changed
        await using var second = new TestHost(_host.DatabaseName, bootstrapAdmin: false);
        Assert.Equal(1, await _host.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers"));
        var again = await second.LoginAsync(TestHost.AdminEmail, TestHost.AdminPassword);
        Assert.Equal(200, (await again.GetAsync("/api/users")).Status);
    }

    [Fact]
    public async Task Without_bootstrap_credentials_no_account_is_created_and_the_password_is_not_a_default()
    {
        await using var bare = new TestHost(bootstrapAdmin: false);
        bare.Factory.CreateClient(); // force host start (and startup seeding)
        Assert.Equal(0, await bare.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers"));
        Assert.Equal(5, await bare.ScalarAsync("SELECT COUNT(*) FROM AspNetRoles")); // roles are still seeded
    }

    [Fact]
    public async Task Bootstrap_never_takes_over_an_existing_non_admin_account_and_never_adds_a_second_admin()
    {
        await using var fresh = new TestHost(bootstrapAdmin: false);
        var anon = new ApiUser("anon", fresh.Factory.CreateClient());
        Assert.Equal(200, (await anon.PostAsync("/api/auth/register",
            new { email = TestHost.AdminEmail, password = "Different!Pass1", displayName = "Squatter" })).Status);

        // now restart WITH bootstrap config that names the same email: must not promote or reset it
        await using var restarted = new TestHost(fresh.DatabaseName, bootstrapAdmin: true);
        restarted.Factory.CreateClient();
        Assert.Equal(0, await fresh.ScalarAsync(
            "SELECT COUNT(*) FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id = ur.RoleId WHERE r.Name = 'Administrator'"));
        Assert.Equal(1, await fresh.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers"));
        var login = await new ApiUser("a", restarted.Factory.CreateClient())
            .PostAsync("/api/auth/login", new { email = TestHost.AdminEmail, password = TestHost.AdminPassword });
        Assert.Equal(401, login.Status); // the bootstrap password did not overwrite the existing account
    }

    // ---- Admin user management: authorization -------------------------------------------

    [Fact]
    public async Task User_management_requires_authentication_and_the_Administrator_role()
    {
        var anon = new ApiUser("anon", _host.Factory.CreateClient());
        var target = await NewUserAsync("Developer");
        Assert.Equal(401, (await anon.GetAsync("/api/users")).Status);
        Assert.Equal(401, (await anon.PostAsync("/api/users", new { })).Status);
        Assert.Equal(401, (await anon.SendAsync(HttpMethod.Put, $"/api/users/{target.Id}/role", new { role = "Administrator" })).Status);

        foreach (var role in AllRoles.Where(r => r != "Administrator"))
        {
            var u = await NewUserAsync(role);
            Assert.Equal(403, (await u.Client.GetAsync("/api/users")).Status);
            Assert.Equal(403, (await u.Client.PostAsync("/api/users",
                new { email = $"{Guid.NewGuid():N}@x.test", password = TestHost.UserPassword, displayName = "x", role = "Administrator" })).Status);
            // privilege escalation attempts: promote self or someone else
            Assert.Equal(403, (await u.Client.SendAsync(HttpMethod.Put, $"/api/users/{u.Id}/role", new { role = "Administrator" })).Status);
            Assert.Equal(403, (await u.Client.SendAsync(HttpMethod.Put, $"/api/users/{target.Id}/role", new { role = "Administrator" })).Status);
            Assert.Equal(403, (await u.Client.SendAsync(HttpMethod.Put, $"/api/users/{target.Id}/status", new { isActive = false })).Status);
        }

        // none of those attempts changed anything
        Assert.Equal(0, await _host.ScalarAsync(
            "SELECT COUNT(*) FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id = ur.RoleId JOIN AspNetUsers u ON u.Id = ur.UserId WHERE r.Name = 'Administrator' AND u.Email <> '" + TestHost.AdminEmail + "'"));
    }

    // ---- Admin user management: behaviour -----------------------------------------------

    [Fact]
    public async Task Admin_can_create_a_user_with_each_of_the_five_roles_and_the_role_is_enforced()
    {
        foreach (var role in AllRoles)
        {
            var u = await NewUserAsync(role);
            var (s, list) = await _admin.GetAsync("/api/users");
            Assert.Equal(200, s);
            Assert.Contains(list!.AsArray(), x => Id(x) == u.Id && x!["role"]!.GetValue<string>() == role && x["isActive"]!.GetValue<bool>());

            // role lands in the token and is enforced: only Administrator/BusinessAnalyst may create projects
            var expected = role is "Administrator" or "BusinessAnalyst" ? 201 : 403;
            Assert.Equal(expected, (await u.Client.PostAsync("/api/projects", new { name = "p-" + role })).Status);
            Assert.Equal(200, (await u.Client.GetAsync("/api/projects")).Status);
        }
    }

    [Fact]
    public async Task Create_user_rejects_invalid_role_weak_password_and_duplicate_email()
    {
        string E() => $"{Guid.NewGuid():N}@x.test";
        Assert.Equal(400, (await _admin.PostAsync("/api/users", new { email = E(), password = TestHost.UserPassword, displayName = "x", role = "Manager" })).Status);
        Assert.Equal(400, (await _admin.PostAsync("/api/users", new { email = E(), password = TestHost.UserPassword, displayName = "x", role = "" })).Status);
        Assert.Equal(400, (await _admin.PostAsync("/api/users", new { email = E(), password = "short", displayName = "x", role = "QA" })).Status);
        Assert.Equal(400, (await _admin.PostAsync("/api/users", new { email = E(), password = "alllowercase1", displayName = "x", role = "QA" })).Status);
        Assert.Equal(400, (await _admin.PostAsync("/api/users", new { email = "nope", password = TestHost.UserPassword, displayName = "x", role = "QA" })).Status);
        Assert.Equal(400, (await _admin.PostAsync("/api/users", new { email = TestHost.AdminEmail, password = TestHost.UserPassword, displayName = "dup", role = "QA" })).Status);
    }

    [Fact]
    public async Task Assigning_a_role_to_a_self_registered_account_activates_it()
    {
        var email = $"{Guid.NewGuid():N}@signup.test";
        var anon = new ApiUser("anon", _host.Factory.CreateClient());
        var (_, reg) = await anon.PostAsync("/api/auth/register", new { email, password = TestHost.UserPassword, displayName = "New Hire" });
        var id = reg!["userId"]!.GetValue<string>();
        var before = await _host.LoginAsync(email, TestHost.UserPassword);
        Assert.Equal(403, (await before.GetAsync("/api/projects")).Status);

        var (s, updated) = await _admin.SendAsync(HttpMethod.Put, $"/api/users/{id}/role", new { role = "Reviewer" });
        Assert.Equal(200, s);
        Assert.Equal("Reviewer", updated!["role"]!.GetValue<string>());

        var after = await _host.LoginAsync(email, TestHost.UserPassword);
        Assert.Equal(200, (await after.GetAsync("/api/projects")).Status);
    }

    [Fact]
    public async Task Role_change_takes_effect_immediately_even_for_tokens_already_issued()
    {
        var u = await NewUserAsync("BusinessAnalyst");
        Assert.Equal(201, (await u.Client.PostAsync("/api/projects", new { name = "before" })).Status);

        var (s, body) = await _admin.SendAsync(HttpMethod.Put, $"/api/users/{u.Id}/role", new { role = "Developer" });
        Assert.Equal(200, s);
        Assert.Equal("Developer", body!["role"]!.GetValue<string>());

        // the old BA token (still unexpired, still carrying the BA role claim) must no longer work
        Assert.Equal(401, (await u.Client.PostAsync("/api/projects", new { name = "after" })).Status);
        Assert.Equal(401, (await u.Client.GetAsync("/api/projects")).Status);

        // a fresh sign-in gets the new, lower role: reads work, project creation is refused
        var relog = await _host.LoginAsync(u.Email, TestHost.UserPassword);
        Assert.Equal(200, (await relog.GetAsync("/api/projects")).Status);
        Assert.Equal(403, (await relog.PostAsync("/api/projects", new { name = "after" })).Status);
        Assert.Equal(1, await _host.ScalarAsync(
            $"SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = '{u.Id}'")); // exactly one role, no accumulation
    }

    [Fact]
    public async Task Deactivated_user_is_signed_out_immediately_cannot_sign_in_and_can_be_reactivated()
    {
        var u = await NewUserAsync("QA");
        Assert.Equal(200, (await u.Client.GetAsync("/api/projects")).Status);

        var (s, off) = await _admin.SendAsync(HttpMethod.Put, $"/api/users/{u.Id}/status", new { isActive = false });
        Assert.Equal(200, s);
        Assert.False(off!["isActive"]!.GetValue<bool>());

        Assert.Equal(401, (await u.Client.GetAsync("/api/projects")).Status);
        var loginAttempt = await new ApiUser("a", _host.Factory.CreateClient()).PostAsync("/api/auth/login", new { email = u.Email, password = TestHost.UserPassword });
        Assert.Equal(403, loginAttempt.Status);

        var (rs, on) = await _admin.SendAsync(HttpMethod.Put, $"/api/users/{u.Id}/status", new { isActive = true });
        Assert.Equal(200, rs);
        Assert.True(on!["isActive"]!.GetValue<bool>());
        var back = await _host.LoginAsync(u.Email, TestHost.UserPassword);
        Assert.Equal(200, (await back.GetAsync("/api/projects")).Status);
    }

    [Fact]
    public async Task Admin_cannot_change_or_deactivate_themselves_so_an_admin_always_remains()
    {
        var (_, list) = await _admin.GetAsync("/api/users");
        var me = Id(list!.AsArray().Single(u => u!["email"]!.GetValue<string>() == TestHost.AdminEmail));

        Assert.Equal(409, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{me}/role", new { role = "QA" })).Status);
        Assert.Equal(409, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{me}/status", new { isActive = false })).Status);
        Assert.Equal(200, (await _admin.GetAsync("/api/users")).Status); // still signed in and still admin
    }

    [Fact]
    public async Task Unknown_user_and_invalid_role_on_update_are_handled()
    {
        var u = await NewUserAsync("QA");
        Assert.Equal(404, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{Guid.NewGuid()}/role", new { role = "QA" })).Status);
        Assert.Equal(404, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{Guid.NewGuid()}/status", new { isActive = false })).Status);
        Assert.Equal(400, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{u.Id}/role", new { role = "SuperUser" })).Status);
        Assert.Equal(400, (await _admin.SendAsync(HttpMethod.Put, $"/api/users/{u.Id}/role", new { role = "" })).Status);
    }

    // ---- Token integrity ------------------------------------------------------------------

    [Fact]
    public async Task Forged_or_stamp_less_tokens_are_rejected()
    {
        // a token claiming the Administrator role but not signed with the server's key
        var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL3JvbGUiOiJBZG1pbmlzdHJhdG9yIn0.invalid-signature");
        Assert.Equal(401, (await new ApiUser("forged", client).GetAsync("/api/users")).Status);
    }
}
