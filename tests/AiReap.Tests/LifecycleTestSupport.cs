using System.Text.Json.Nodes;
using AiReap.Application.Artifacts;
using AiReap.Application.Common;
using AiReap.Application.Lifecycle;
using AiReap.Application.Persistence;
using AiReap.Application.Traceability;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace AiReap.Tests;

// A registered user: the HTTP client, their id and the one role they hold.
public record Actor(ApiUser Api, string Id, string Role);

public sealed class TestCurrentUser(string userId, params string[] roles) : ICurrentUser
{
    public string UserId { get; } = userId;
    public bool IsInRole(string role) => roles.Contains(role);
}

// The lifecycle / work-record services have no endpoints until lifecycle phase 3, so tests call
// them directly, as a given user, over the test host's real database and DI container. Every
// property access builds the service on a fresh DI scope (its own DbContext), like one HTTP request.
public sealed class ServicesAs : IDisposable
{
    private readonly TestHost _host;
    private readonly ICurrentUser _user;
    private readonly List<IServiceScope> _scopes = [];

    public ServicesAs(TestHost host, ICurrentUser user)
    {
        _host = host;
        _user = user;
    }

    private (IAiReapDbContext Db, IServiceProvider Sp, ProjectAccessService Access) NewRequest()
    {
        var scope = _host.Factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<IAiReapDbContext>();
        return (db, scope.ServiceProvider, new ProjectAccessService(db, _user));
    }

    private static LifecycleEvidenceService EvidenceFor(IAiReapDbContext db, IServiceProvider sp) =>
        new(db, sp.GetRequiredService<IUserRoleDirectory>());

    public ILifecycleEvidenceService Evidence
    {
        get { var r = NewRequest(); return EvidenceFor(r.Db, r.Sp); }
    }

    public IProjectLifecycleService Lifecycle
    {
        get
        {
            var r = NewRequest();
            return new ProjectLifecycleService(r.Db, _user, r.Access, EvidenceFor(r.Db, r.Sp),
                new LifecycleNotifications(r.Db, r.Sp.GetRequiredService<IUserRoleDirectory>()));
        }
    }

    public IArtifactWorkService Work
    {
        get { var r = NewRequest(); return new ArtifactWorkService(r.Db, _user, r.Access); }
    }

    public IArtifactService Artifacts
    {
        get { var r = NewRequest(); return new ArtifactService(r.Db, _user, r.Access, r.Sp.GetRequiredService<IImpactAnalysisService>()); }
    }

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
    }
}

public static class LifecycleTestSupport
{
    public static async Task<Actor> ActorAsync(this TestHost host, string role)
    {
        var api = await host.RegisterAsync(role);
        return new Actor(api, await host.UserIdAsync(api.Email), role);
    }

    public static async Task<Actor> AdminAsync(this TestHost host)
    {
        var api = await host.LoginAsync(TestHost.AdminEmail, TestHost.AdminPassword);
        return new Actor(api, await host.UserIdAsync(TestHost.AdminEmail), "Administrator");
    }

    public static async Task<string> UserIdAsync(this TestHost host, string email)
    {
        await using var conn = new SqlConnection(host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Id FROM AspNetUsers WHERE NormalizedEmail = UPPER(@e)", conn);
        cmd.Parameters.AddWithValue("@e", email);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public static ServicesAs As(this TestHost host, Actor actor) => new(host, new TestCurrentUser(actor.Id, actor.Role));

    public static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();
}
