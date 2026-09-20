using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using AiReap.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiReap.Tests;

// Wraps the real stub AI client so a test can make specific pipeline prompts fail. This is how
// the retry path is exercised without any production-side test hook.
public class ControllableAiChatClient : IAiChatClient
{
    private readonly StubAiChatClient _inner = new();
    private readonly List<string> _failMarkers = new();

    public string ModelName => _inner.ModelName;

    // The next call whose system prompt contains this marker throws, once.
    public void FailOnceWhenPromptContains(string marker)
    {
        lock (_failMarkers) _failMarkers.Add(marker);
    }

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        lock (_failMarkers)
        {
            var hit = _failMarkers.FirstOrDefault(systemPrompt.Contains);
            if (hit is not null)
            {
                _failMarkers.Remove(hit);
                throw new InvalidOperationException($"Simulated AI provider failure ({hit}).");
            }
        }

        return _inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
    }
}

// One throwaway SQL Server database per instance, created by applying the real EF migrations
// (so the migrations themselves are under test). Two hosts can share one database to simulate a restart.
public class TestHost : IAsyncDisposable
{
    public const string ServerConnection = "Server=127.0.0.1,14330;User Id=sa;Password=AiReap!DevPassw0rd;TrustServerCertificate=True";

    public string DatabaseName { get; }
    public string ConnectionString => $"{ServerConnection};Database={DatabaseName}";
    public ControllableAiChatClient Ai { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; }
    private readonly bool _ownsDatabase;

    public const string AdminEmail = "admin@bootstrap.test";
    public const string AdminPassword = "B00tstrap!Admin";
    public const string UserPassword = "Passw0rd!123";

    private readonly bool _bootstrap;
    private ApiUser? _admin;

    public TestHost(string? existingDatabase = null, bool bootstrapAdmin = true)
    {
        _bootstrap = bootstrapAdmin;
        _ownsDatabase = existingDatabase is null;
        DatabaseName = existingDatabase ?? $"AiReapTests_{Guid.NewGuid():N}";
        if (_ownsDatabase)
        {
            // Must precede host start: the API seeds roles on startup in Development.
            using var db = new AiReapDbContext(
                new DbContextOptionsBuilder<AiReapDbContext>().UseSqlServer(ConnectionString).Options);
            db.Database.Migrate();
        }

        Factory = Build();
    }

    private WebApplicationFactory<Program> Build() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); // seeds roles, same as a real dev run
            b.UseSetting("ConnectionStrings:Default", ConnectionString);
            if (_bootstrap)
            {
                b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
                b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
            }
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IAiChatClient>();
                s.AddSingleton<IAiChatClient>(Ai);
            });
        });

    // Users are created the way production does it: the bootstrap administrator signs in and
    // uses the admin users API. (Public sign-up can no longer grant a role.)
    public async Task<ApiUser> RegisterAsync(string role)
    {
        _admin ??= await LoginAsync(AdminEmail, AdminPassword);
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@test.io";
        var (status, body) = await _admin.PostAsync("/api/users",
            new { email, password = UserPassword, displayName = role, role });
        if (status != 201) throw new InvalidOperationException($"create {role} failed: {status} {body}");
        return await LoginAsync(email, UserPassword);
    }

    public async Task<ApiUser> LoginAsync(string email, string password)
    {
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
        var authed = Factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new ApiUser("login", authed);
    }

    public async Task<int> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (!_ownsDatabase) return;
        await using var conn = new SqlConnection(ServerConnection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class ApiUser
{
    public string Role { get; }
    public HttpClient Http { get; }

    public ApiUser(string role, HttpClient http)
    {
        Role = role;
        Http = http;
    }

    public async Task<(int Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return ((int)res.StatusCode, null);
        try { return ((int)res.StatusCode, JsonNode.Parse(text)); }
        catch (System.Text.Json.JsonException) { return ((int)res.StatusCode, JsonValue.Create(text)); } // e.g. plain-text 500 page
    }

    public Task<(int Status, JsonNode? Body)> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
    public Task<(int Status, JsonNode? Body)> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    public Task<(int Status, JsonNode? Body)> DecideAsync(string runId, bool approve, string? comment = null) =>
        PostAsync($"/api/agent-runs/{runId}/decision", new { approve, comment });
}

public static class AgentStatus
{
    // AgentRunStatus
    public const int RunRunning = 0, RunAwaiting = 1, RunCompleted = 2, RunRejected = 3, RunFailed = 4;
    // AgentStageStatus
    public const int Pending = 0, Running = 1, Awaiting = 2, Approved = 3, Rejected = 4, Failed = 5;
}
