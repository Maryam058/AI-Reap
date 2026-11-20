using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiReap.Infrastructure.Ai;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiReap.Tests;

// G1 - Administrator AI configuration and system settings (GET /api/settings). Administrator-only
// on the server, read-only, and composed from the existing /api/ai/status health check; provider
// credentials must never appear in the response.
public class SystemSettingsTests : IAsyncLifetime
{
    // Plausible-length fakes (HasUsableApiKey requires >= 30 chars), so the providers count as configured.
    private static readonly string GeminiKey = "AIzaTESTGEMINI" + Guid.NewGuid().ToString("N");
    private static readonly string GroqKey = "gsk_TESTGROQ" + Guid.NewGuid().ToString("N");

    private readonly TestHost _host = new();
    private ApiUser _admin = null!;

    public async Task InitializeAsync() => _admin = await _host.LoginAsync(TestHost.AdminEmail, TestHost.AdminPassword);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ---- Authorization ------------------------------------------------------------------

    [Fact]
    public async Task Administrator_can_read_settings()
    {
        var (status, body) = await _admin.GetAsync("/api/settings");
        Assert.Equal(200, status);
        Assert.NotNull(body!["ai"]!["status"]);
        Assert.Equal(SystemSettingsService.CredentialsNote, body["credentialsNote"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("BusinessAnalyst")]
    [InlineData("Developer")]
    [InlineData("QA")]
    [InlineData("Reviewer")]
    public async Task Non_administrators_are_forbidden(string role)
    {
        var user = await _host.RegisterAsync(role);
        Assert.Equal(403, (await user.GetAsync("/api/settings")).Status);
    }

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var anon = new ApiUser("anon", _host.Factory.CreateClient());
        Assert.Equal(401, (await anon.GetAsync("/api/settings")).Status);
    }

    [Fact]
    public async Task Role_matrix_reports_settings_as_administrator_only()
    {
        var (_, groups) = await _admin.GetAsync("/api/roles/matrix");
        var roles = groups!.AsArray()
            .Single(g => g!["group"]!.GetValue<string>() == "Administer")!["items"]!.AsArray()
            .Single(i => i!["label"]!.GetValue<string>() == "View AI configuration and system settings")!["roles"]!.AsArray()
            .Select(r => r!.GetValue<string>());
        Assert.Equal(new[] { "Administrator" }, roles);
    }

    // ---- AI status (unconfigured: the test host blanks every key) ------------------------

    [Fact]
    public async Task Existing_ai_status_still_works_and_matches_the_settings_view()
    {
        var (status, aiStatus) = await _admin.GetAsync("/api/ai/status");
        Assert.Equal(200, status);
        Assert.Equal("Gemini", aiStatus!["provider"]!.GetValue<string>());
        Assert.Equal("Groq", aiStatus["fallback"]!["provider"]!.GetValue<string>());

        var (_, settings) = await _admin.GetAsync("/api/settings");
        Assert.Equal(aiStatus.ToJsonString(), settings!["ai"]!["status"]!.ToJsonString());
    }

    [Fact]
    public async Task Unconfigured_providers_are_reported_safely()
    {
        var (_, body) = await _admin.GetAsync("/api/settings");
        var ai = body!["ai"]!;
        var primary = ai["status"]!;
        Assert.Equal("Gemini", primary["provider"]!.GetValue<string>());
        Assert.False(primary["configured"]!.GetValue<bool>());
        Assert.False(primary["modelAvailable"]!.GetValue<bool>());
        Assert.Contains("Ai:Gemini:ApiKey is not configured", primary["detail"]!.GetValue<string>());

        var fallback = primary["fallback"]!;
        Assert.Equal("Groq", fallback["provider"]!.GetValue<string>());
        Assert.False(fallback["configured"]!.GetValue<bool>());
        Assert.False(fallback["modelAvailable"]!.GetValue<bool>());

        Assert.True(ai["fallbackEnabled"]!.GetValue<bool>());
        Assert.True(ai["usingDevelopmentStub"]!.GetValue<bool>());
        Assert.Null(ai["primaryCooldownUntil"]);

        var runtime = body["runtime"]!;
        Assert.Equal("Development", runtime["environment"]!.GetValue<string>());
        Assert.Equal(600, runtime["primaryCooldownSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Current_primary_cooldown_is_reported()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        _host.Factory.Services.GetRequiredService<AiProviderCooldown>().CoolDown("Gemini", until);

        var (_, body) = await _admin.GetAsync("/api/settings");
        var reported = body!["ai"]!["primaryCooldownUntil"]!.GetValue<DateTimeOffset>();
        Assert.Equal(until.ToUnixTimeSeconds(), reported.ToUnixTimeSeconds());
    }

    // ---- Configured providers + secret safety (fake HTTP, never a live API) --------------

    [Fact]
    public async Task Configured_gemini_primary_and_groq_fallback_are_represented_without_secrets()
    {
        var handler = new FakeProviderHandler(HttpStatusCode.OK);
        await using var factory = WithProviderKeys(handler);
        var admin = await LoginAsync(factory);

        var settingsJson = await admin.GetStringAsync("/api/settings");
        var statusJson = await admin.GetStringAsync("/api/ai/status");

        var ai = JsonNode.Parse(settingsJson)!["ai"]!;
        var primary = ai["status"]!;
        Assert.Equal("Gemini", primary["provider"]!.GetValue<string>());
        Assert.Equal(new GeminiOptions().Model, primary["model"]!.GetValue<string>());
        Assert.True(primary["configured"]!.GetValue<bool>());
        Assert.True(primary["modelAvailable"]!.GetValue<bool>());

        var fallback = primary["fallback"]!;
        Assert.Equal("Groq", fallback["provider"]!.GetValue<string>());
        Assert.Equal(new GroqOptions().Model, fallback["model"]!.GetValue<string>());
        Assert.True(fallback["configured"]!.GetValue<bool>());
        Assert.True(fallback["modelAvailable"]!.GetValue<bool>());
        Assert.False(ai["usingDevelopmentStub"]!.GetValue<bool>());

        // The keys really were loaded (the health check sent them to the fake providers)...
        Assert.Contains(handler.SentHeaders, h => h.Contains(GeminiKey));
        Assert.Contains(handler.SentHeaders, h => h.Contains(GroqKey));

        // ...yet neither response carries them, nor any serialized options/config object.
        foreach (var json in new[] { settingsJson, statusJson })
        {
            Assert.DoesNotContain(GeminiKey, json);
            Assert.DoesNotContain(GroqKey, json);
            Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hasUsableApiKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("baseUrl", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("maxOutputTokens", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", json);
            Assert.DoesNotContain(_host.DatabaseName, json);
            Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Server=", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Configured_but_unavailable_model_is_reported_safely()
    {
        await using var factory = WithProviderKeys(new FakeProviderHandler(HttpStatusCode.NotFound));
        var admin = await LoginAsync(factory);

        var json = await admin.GetStringAsync("/api/settings");
        var primary = JsonNode.Parse(json)!["ai"]!["status"]!;
        Assert.True(primary["configured"]!.GetValue<bool>());
        Assert.False(primary["modelAvailable"]!.GetValue<bool>());
        Assert.Contains("is not available", primary["detail"]!.GetValue<string>());
        Assert.False(primary["fallback"]!["modelAvailable"]!.GetValue<bool>());
        Assert.DoesNotContain(GeminiKey, json);
        Assert.DoesNotContain(GroqKey, json);
    }

    // Same database and stub chat client as _host, plus real-looking keys. The health check's
    // HTTP goes to a fake handler, so no live provider is ever called.
    private WebApplicationFactory<Program> WithProviderKeys(FakeProviderHandler handler) =>
        _host.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ai:Gemini:ApiKey", GeminiKey);
            b.UseSetting("Ai:Groq:ApiKey", GroqKey);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IHttpClientFactory>();
                s.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });
        });

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = TestHost.AdminEmail, password = TestHost.AdminPassword });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
        var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return authed;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeProviderHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> SentHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (SentHeaders) SentHeaders.Add(request.Headers.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
