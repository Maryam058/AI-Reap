using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using AiReap.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiReap.Tests;

// Wraps the real stub AI client so a test can make specific pipeline prompts fail. This is how
// the retry path is exercised without any production-side test hook.
public class ControllableAiChatClient : IAiChatClient
{
    private readonly StubAiChatClient _inner = new();
    private readonly List<(string Marker, Func<string> Outcome)> _scripted = new();

    public string ModelName => _inner.ModelName;

    // Every user prompt received, in order (lets a test see e.g. the repair instruction).
    public List<string> UserPrompts { get; } = new();

    // The next call whose system prompt contains this marker throws, once.
    public void FailOnceWhenPromptContains(string marker) =>
        Script(marker, () => throw new InvalidOperationException($"Simulated AI provider failure ({marker})."));

    // The next call whose system prompt contains this marker throws the given exception, once.
    public void ThrowOnceWhenPromptContains(string marker, Exception exception) => Script(marker, () => throw exception);

    // The next call whose system prompt contains this marker returns this raw text, once.
    public void RespondOnceWhenPromptContains(string marker, string rawResponse) => Script(marker, () => rawResponse);

    private void Script(string marker, Func<string> outcome)
    {
        lock (_scripted) _scripted.Add((marker, outcome));
    }

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        Func<string>? outcome = null;
        lock (_scripted)
        {
            UserPrompts.Add(userPrompt);
            var index = _scripted.FindIndex(s => systemPrompt.Contains(s.Marker));
            if (index >= 0)
            {
                outcome = _scripted[index].Outcome;
                _scripted.RemoveAt(index);
            }
        }

        return outcome is not null ? Task.FromResult(outcome()) : _inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
    }
}

// Offline-test seams for TestHost's own construction and disposal paths.
internal sealed record TestHostSeams(Action? AfterConstruction = null, Func<ValueTask>? DisposeFactory = null);

// One throwaway SQL Server database per instance, created by the SQL test guard (TestSqlServer) and then
// migrated with the real EF migrations (so the migrations themselves are under test). Two hosts can share
// one database to simulate a restart: the second holds a lease and never drops it.
//
// The server comes only from AIREAP_TEST_SQL_CONNECTION and AIREAP_TEST_SQL_EXPECTED_SERVER; there is no
// fallback to the developer's user-secrets (see TestSqlSettings).
public class TestHost : IAsyncDisposable
{
    private readonly TestSqlServer _sql;
    private readonly TestDatabase? _owned;
    private readonly TestDatabaseLease? _lease;
    private readonly Func<ValueTask>? _disposeFactory;
    private Task? _disposal;

    public string DatabaseName { get; }

    // The string the API host and the raw-connection tests use. Content comes only from the validated
    // settings plus this guard-created database, and no new string is handed out once the database is no
    // longer Ready. But a string, once handed out, cannot be revoked (the API's configuration, test
    // variables and SqlClient pools may hold copies), and connections opened from it are not re-verified
    // on every open - only the guard's own connections (OpenVerifiedConnectionAsync, ScalarAsync,
    // ExecuteAsync, migrations) are. A stale or misrouted open fails at login (4060) unless the target
    // happens to hold a database with the same random name.
    public string ConnectionString => _sql.ConnectionStringFor(DatabaseName);
    public ControllableAiChatClient Ai { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; }

    public const string AdminEmail = "admin@bootstrap.test";
    public const string AdminPassword = "B00tstrap!Admin";
    public const string UserPassword = "Passw0rd!123";

    private readonly bool _bootstrap;
    private readonly IAiChatClient? _chatClient;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private ApiUser? _admin;

    // chatClient replaces the scripted stub, for tests that need the real provider stack (over fake HTTP).
    // settings: extra configuration for this host only (e.g. Ai:Groq:MaxBatchedRunParts).
    public TestHost(string? existingDatabase = null, bool bootstrapAdmin = true, IAiChatClient? chatClient = null,
        IReadOnlyDictionary<string, string>? settings = null)
        : this(() => TestSqlServer.Default, existingDatabase, bootstrapAdmin, chatClient, settings, seams: null)
    {
    }

    internal TestHost(Func<TestSqlServer> sqlServer, string? existingDatabase, bool bootstrapAdmin, IAiChatClient? chatClient,
        IReadOnlyDictionary<string, string>? settings, TestHostSeams? seams)
    {
        _settings = settings ?? new Dictionary<string, string>();
        _bootstrap = bootstrapAdmin;
        _chatClient = chatClient;
        _disposeFactory = seams?.DisposeFactory;
        _sql = sqlServer(); // missing or unsafe settings throw here, before any connection is attempted

        if (existingDatabase is null)
        {
            _owned = _sql.CreateDatabaseAsync().GetAwaiter().GetResult();
            DatabaseName = _owned.Name;
            try
            {
                // Must precede host start: the API seeds roles on startup in Development.
                RunOnVerifiedConnection(connection =>
                {
                    using var db = new AiReapDbContext(connection.CreateMigrationOptions());
                    db.Database.Migrate();
                });
                Factory = Build();
                seams?.AfterConstruction?.Invoke();
            }
            catch (Exception ex)
            {
                // A constructor that throws never reaches DisposeAsync: drop (guarded) here.
                CleanUpAfterFailedConstruction(ex, _owned.Dispose);
                throw;
            }
        }
        else
        {
            _lease = _sql.AcquireLease(existingDatabase);
            DatabaseName = existingDatabase;
            try
            {
                // Fail now, not mid-test, if the shared database is no longer the one this run created.
                RunOnVerifiedConnection(static _ => { });
                Factory = Build();
                seams?.AfterConstruction?.Invoke();
            }
            catch (Exception ex)
            {
                CleanUpAfterFailedConstruction(ex, _lease.Dispose);
                throw;
            }
        }
    }

    // The verified connection is always disposed before this returns, so its reservation is gone before any
    // owner cleanup. A failure to dispose it is reported together with the work's own failure, never instead.
    private void RunOnVerifiedConnection(Action<VerifiedConnection> work)
    {
        var connection = _sql.OpenVerifiedAsync(DatabaseName).GetAwaiter().GetResult();
        Exception? failure = null;
        try
        {
            work(connection);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            connection.Dispose();
        }
        catch (Exception disposeFailure) when (failure is not null)
        {
            throw new AggregateException(failure, disposeFailure);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void CleanUpAfterFailedConstruction(Exception failure, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
    }

    private WebApplicationFactory<Program> Build() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Read when the host is built: throws if the database is no longer Ready.
            var connectionString = ConnectionString;

            b.UseEnvironment("Development"); // seeds roles, same as a real dev run

            // The "Development" environment auto-loads the developer's own `dotnet user-secrets`
            // for AiReap.Api (UserSecretsId "AiReap.Api"), which can carry a real Bootstrap:AdminEmail/
            // AdminPassword for manual local testing. Left in place, that leaks into every test host
            // regardless of what this class configures below, silently creating/promoting an
            // administrator the test never asked for. Strip it so tests only ever see what TestHost
            // itself sets - the same isolation a CI machine (with no user secrets at all) gets for free.
            b.ConfigureAppConfiguration((_, config) =>
            {
                var userSecretsSources = config.Sources
                    .OfType<JsonConfigurationSource>()
                    .Where(s => s.Path?.Contains("UserSecrets", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
                foreach (var source in userSecretsSources) config.Sources.Remove(source);

                // Last source wins: nothing earlier (appsettings, a ConnectionStrings__Default environment
                // variable) can point the API at another database. Checked again in ConfigureServices.
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString });
            });

            b.UseSetting("ConnectionStrings:Default", connectionString);

            // Automated tests never call a live AI API, even if a developer has Ai__Gemini__ApiKey
            // (or another provider key) in their environment: chat is the stub (replaced below),
            // and blank keys keep embeddings on the deterministic stub too.
            b.UseSetting("Ai:Provider", "Gemini");
            b.UseSetting("Ai:Gemini:ApiKey", "");
            b.UseSetting("Ai:Groq:ApiKey", "");
            b.UseSetting("GROQ_API_KEY", ""); // the machine-level key must not reach tests either
            b.UseSetting("Ai:Anthropic:ApiKey", "");
            foreach (var (key, value) in _settings) b.UseSetting(key, value);
            b.UseSetting("Ai:OpenAI:ApiKey", "");
            if (_bootstrap)
            {
                b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
                b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
            }
            else
            {
                // Explicitly blank, not just "not set" - guarantees no ambient config (env vars,
                // machine-wide config, etc.) can supply bootstrap credentials for a test that
                // specifically wants "no bootstrap configured" behavior.
                b.UseSetting("Bootstrap:AdminEmail", "");
                b.UseSetting("Bootstrap:AdminPassword", "");
            }
            b.ConfigureServices((ctx, s) =>
            {
                // Runs inside the host build, before RoleSeeder sends anything to the database.
                if (!string.Equals(ctx.Configuration.GetConnectionString("Default"), connectionString, StringComparison.Ordinal))
                {
                    throw new TestSqlGuardException(
                        $"The API host's ConnectionStrings:Default does not resolve to the guard-issued string for '{DatabaseName}'; refusing to start it.");
                }

                s.RemoveAll<IAiChatClient>();
                s.AddSingleton<IAiChatClient>(_chatClient ?? Ai);
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
        return new ApiUser("login", authed) { Email = email };
    }

    // §38 DoD — project membership. `owner` must already be a member (normally the project's
    // creator) with a writer role, since POST .../members is BA/Admin-only.
    public Task<(int Status, JsonNode? Body)> AddMemberAsync(ApiUser owner, string projectId, ApiUser member) =>
        owner.PostAsync($"/api/projects/{projectId}/members", new { email = member.Email });

    // A connection whose server, database and run token were verified on the same session. Each call is a
    // fresh non-pooled login; dispose it promptly (it holds a reservation that blocks the owner's DROP).
    public Task<VerifiedConnection> OpenVerifiedConnectionAsync() => _sql.OpenVerifiedAsync(DatabaseName);

    public async Task<int> ScalarAsync(string sql)
    {
        await using var conn = await OpenVerifiedConnectionAsync();
        await using var cmd = conn.CreateCommand(sql);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = await OpenVerifiedConnectionAsync();
        await using var cmd = conn.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    // Concurrent and repeated calls await the same disposal. The API host goes first; the lease release or
    // the guarded drop always runs afterwards, even if disposing the host failed, and every failure is
    // reported.
    public ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _disposal, completion.Task, null);
        if (existing is not null)
        {
            return new ValueTask(existing);
        }

        _ = DisposeOnceAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeOnceAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try
        {
            await (_disposeFactory is not null ? _disposeFactory() : Factory.DisposeAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            _lease?.Dispose();
            if (_owned is not null)
            {
                await _owned.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count == 0)
        {
            completion.SetResult();
        }
        else
        {
            completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }
}

public class ApiUser
{
    public string Role { get; }
    public HttpClient Http { get; }
    public string Email { get; init; } = "";

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
