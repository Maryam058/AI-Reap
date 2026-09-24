using AiReap.Application.Ai;
using AiReap.Infrastructure;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiReap.Tests;

// Covers AddInfrastructure's choice of IAiChatClient implementation (AiReap.Infrastructure/
// DependencyInjection.cs), which used to be a single "is ApiKey blank?" check. A leftover
// placeholder value (e.g. "sk-ant-..." from setup docs) is not blank, so it passed that check
// straight through to a real HTTP call that's guaranteed to fail with 401 - the actual root
// cause traced in this session's Analyze 500 investigation. These tests exercise the real,
// public AddInfrastructure entry point end to end (not the private selector directly), the same
// way Program.cs calls it.
public class AiChatClientSelectionTests
{
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "AiReap.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // Captures formatted log messages without needing a mocking library, consistent with the
    // hand-rolled fakes already used elsewhere in this suite (ControllableAiChatClient, FakeHandler).
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<string> _messages;
            public CapturingLogger(List<string> messages) => _messages = messages;

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_messages) _messages.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose()
                {
                }
            }
        }
    }

    // A syntactically plausible key: long, sk-ant- prefixed, no ellipsis - what a real Anthropic
    // key looks like, without being one.
    private const string PlausibleRealKey = "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // A syntactically plausible Gemini key ("AIza" + 35 chars) that is not a real key.
    private const string PlausibleGeminiKey = "AIzaSyAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // Gemini is now the default provider; the Anthropic-specific tests below pin "Anthropic" explicitly.
    private static (ServiceProvider Provider, CapturingLoggerProvider Logs) BuildProvider(
        string? apiKey, string environmentName, string? aiProvider = "Anthropic", string? geminiKey = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Server=unused;Database=unused;Trusted_Connection=True",
                ["Ai:Provider"] = aiProvider,
                ["Ai:Anthropic:ApiKey"] = apiKey,
                ["Ai:Anthropic:Model"] = "claude-sonnet-5",
                ["Ai:Gemini:ApiKey"] = geminiKey,
            })
            .Build();

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment { EnvironmentName = environmentName });
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddInfrastructure(configuration);

        return (services.BuildServiceProvider(), logs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-ant-...")]
    public async Task AddInfrastructure_uses_the_stub_client_in_development_when_no_usable_key_is_configured(string? apiKey)
    {
        var (provider, logs) = BuildProvider(apiKey, Environments.Development);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.IsType<StubAiChatClient>(client);
        Assert.Contains(logs.Messages, m => m.Contains("Anthropic API key not configured; using development AI stub."));
    }

    [Fact]
    public async Task AddInfrastructure_uses_the_stub_client_in_the_testing_environment_when_no_usable_key_is_configured()
    {
        var (provider, _) = BuildProvider(apiKey: null, "Testing");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.IsType<StubAiChatClient>(client);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AddInfrastructure_uses_the_real_client_whenever_a_usable_api_key_is_configured(string environmentName)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, environmentName);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.IsType<AnthropicAiChatClient>(Assert.IsType<ResilientAiChatClient>(client).Inner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-ant-...")]
    public async Task AddInfrastructure_throws_in_production_instead_of_silently_using_the_stub_when_no_usable_key_is_configured(string? apiKey)
    {
        var (provider, _) = BuildProvider(apiKey, Environments.Production);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());

        Assert.Contains("Ai:Anthropic:ApiKey", ex.Message);
        Assert.Contains("Production", ex.Message);
        if (!string.IsNullOrEmpty(apiKey))
        {
            Assert.DoesNotContain(apiKey, ex.Message, StringComparison.Ordinal); // never echoes the key itself
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("sk-ant-...", false)] // the exact placeholder that caused the original 401
    [InlineData("short-key", false)]
    [InlineData(PlausibleRealKey, true)]
    public void HasUsableApiKey_rejects_blank_and_placeholder_looking_keys(string? apiKey, bool expected)
    {
        var options = new AnthropicOptions { ApiKey = apiKey ?? string.Empty };

        Assert.Equal(expected, options.HasUsableApiKey);
    }

    // Ollama needs no API key, so Ai:Provider is the only thing that matters here - present or
    // absent Anthropic key must not change the outcome once Ollama is explicitly selected.
    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", null)]
    [InlineData("Production", PlausibleRealKey)]
    public async Task AddInfrastructure_uses_the_ollama_client_when_Ai_Provider_is_Ollama(string environmentName, string? anthropicApiKey)
    {
        var (provider, _) = BuildProvider(anthropicApiKey, environmentName, aiProvider: "Ollama");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.IsType<OllamaAiChatClient>(Assert.IsType<ResilientAiChatClient>(client).Inner);
    }

    [Fact]
    public async Task AddInfrastructure_still_uses_the_anthropic_selection_logic_when_Ai_Provider_is_explicitly_Anthropic()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, aiProvider: "Anthropic");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.IsType<AnthropicAiChatClient>(Assert.IsType<ResilientAiChatClient>(client).Inner);
    }

    [Fact]
    public async Task AddInfrastructure_throws_a_clear_error_when_Ai_Provider_is_unrecognized()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Development, aiProvider: "SomeOtherVendor");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());

        Assert.Contains("Ai:Provider", ex.Message);
        Assert.Contains("SomeOtherVendor", ex.Message);
    }

    // ---- Gemini (the default provider) -------------------------------------------------------

    [Theory]
    [InlineData(null)]          // Ai:Provider unset -> Gemini is the default
    [InlineData("Gemini")]
    [InlineData("gemini")]
    public async Task AddInfrastructure_uses_the_gemini_client_by_default_when_a_key_is_configured(string? aiProvider)
    {
        var (provider, _) = BuildProvider(apiKey: null, Environments.Production, aiProvider, geminiKey: PlausibleGeminiKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        var resilient = Assert.IsType<ResilientAiChatClient>(client);
        Assert.IsType<GeminiAiChatClient>(resilient.Inner);
        Assert.Equal("gemini-3.6-flash", client.ModelName);
    }

    [Fact]
    public void Shipped_appsettings_resolve_to_gemini_3_6_flash_without_any_committed_api_key()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "AiReap.Api", "appsettings.json")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory!.FullName, "src", "AiReap.Api", "appsettings.json"), optional: false)
            .Build();
        var gemini = configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>()!;

        Assert.Equal("Gemini", configuration["Ai:Provider"]);
        Assert.Equal("gemini-3.6-flash", gemini.Model);
        Assert.Equal("low", gemini.ThinkingLevel);
        // Gemini 3 rejects thinkingLevel + thinkingBudget together, and advises against low temperatures.
        Assert.Null(gemini.ThinkingBudget);
        Assert.Null(gemini.Temperature);
        // The key only ever comes from user-secrets or Ai__Gemini__ApiKey.
        Assert.True(string.IsNullOrEmpty(gemini.ApiKey));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task AddInfrastructure_uses_the_stub_for_gemini_in_dev_and_test_when_no_key_is_configured(string environmentName)
    {
        var (provider, logs) = BuildProvider(apiKey: null, environmentName, aiProvider: "Gemini");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType<StubAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.Contains(logs.Messages, m => m.Contains("Gemini API key not configured; using development AI stub."));
    }

    [Fact]
    public async Task Missing_gemini_key_outside_development_never_falls_back_to_the_stub_and_fails_calls_as_not_configured()
    {
        var (provider, _) = BuildProvider(apiKey: null, Environments.Production, aiProvider: "Gemini");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        Assert.IsNotType<StubAiChatClient>(client);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("system", "user"));
        Assert.Equal(AiFailureKind.NotConfigured, ex.Kind);
        Assert.Contains("Ai:Gemini:ApiKey", ex.Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("AIza...", false)]
    [InlineData("your-gemini-api-key-goes-here-please-xx", false)]
    [InlineData("short", false)]
    [InlineData(PlausibleGeminiKey, true)]
    public void Gemini_HasUsableApiKey_rejects_blank_and_placeholder_keys(string? apiKey, bool expected)
    {
        Assert.Equal(expected, new GeminiOptions { ApiKey = apiKey ?? string.Empty }.HasUsableApiKey);
    }
}
