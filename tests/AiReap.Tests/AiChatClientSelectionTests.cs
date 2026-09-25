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
        string? apiKey, string environmentName, string? aiProvider = "Anthropic", string? geminiKey = null,
        string? groqKey = null, IDictionary<string, string?>? extra = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Server=unused;Database=unused;Trusted_Connection=True",
                ["Ai:Provider"] = aiProvider,
                ["Ai:Anthropic:ApiKey"] = apiKey,
                ["Ai:Anthropic:Model"] = "claude-sonnet-5",
                ["Ai:Gemini:ApiKey"] = geminiKey,
                ["Ai:Groq:ApiKey"] = groqKey,
            })
            .AddInMemoryCollection(extra ?? new Dictionary<string, string?>())
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
        Assert.Contains(logs.Messages, m => m.Contains("No AI provider API key configured") && m.Contains("DEVELOPMENT AI STUB"));
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

    // ---- Groq fallback -----------------------------------------------------------------------

    // A syntactically plausible Groq key ("gsk_" + 52 chars) that is not a real key.
    private const string PlausibleGroqKey = "gsk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Gemini_is_primary_and_groq_the_fallback_when_both_keys_are_configured(string environmentName)
    {
        var (provider, _) = BuildProvider(apiKey: null, environmentName, aiProvider: "Gemini", geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = Assert.IsType<FallbackAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>());

        Assert.IsType<GeminiAiChatClient>(Assert.IsType<ResilientAiChatClient>(client.Primary).Inner);
        // Groq alone carries the request budget (Ai:Groq:MaxRequestTokens); Gemini is never budgeted.
        var budgeted = Assert.IsType<RequestBudgetAiChatClient>(client.Fallback);
        Assert.IsType<GroqAiChatClient>(Assert.IsType<ResilientAiChatClient>(budgeted.Inner).Inner);
        Assert.Equal(8000 - 2000, budgeted.InputBudget);
        Assert.Equal("gemini-3.6-flash", client.ModelName); // before any call, the primary
    }

    [Fact]
    public async Task Groq_key_is_read_from_the_conventional_GROQ_API_KEY_setting_when_Ai_Groq_ApiKey_is_unset()
    {
        var (provider, _) = BuildProvider(apiKey: null, Environments.Production, aiProvider: "Gemini", geminiKey: PlausibleGeminiKey,
            extra: new Dictionary<string, string?> { ["GROQ_API_KEY"] = PlausibleGroqKey });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType<FallbackAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
    }

    [Fact]
    public async Task Groq_alone_is_used_when_only_a_groq_key_is_configured_never_the_stub()
    {
        var (provider, logs) = BuildProvider(apiKey: null, Environments.Development, aiProvider: "Gemini", groqKey: PlausibleGroqKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.IsType<GroqAiChatClient>(Assert.IsType<ResilientAiChatClient>(Assert.IsType<RequestBudgetAiChatClient>(client).Inner).Inner);
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName);
        Assert.Contains(logs.Messages, m => m.Contains("using Groq only"));
    }

    [Fact]
    public async Task Fallback_can_be_disabled_with_FallbackProvider_None()
    {
        var (provider, _) = BuildProvider(apiKey: null, Environments.Production, aiProvider: "Gemini", geminiKey: PlausibleGeminiKey,
            groqKey: PlausibleGroqKey, extra: new Dictionary<string, string?> { ["Ai:FallbackProvider"] = "None" });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType<GeminiAiChatClient>(Assert.IsType<ResilientAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>()).Inner);
    }

    [Theory]
    [InlineData("Ollama")]
    [InlineData("ollama")]
    public async Task Ollama_is_no_longer_a_recognized_provider(string aiProvider)
    {
        var (provider, _) = BuildProvider(apiKey: null, Environments.Development, aiProvider);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.Contains("Ai:Provider", ex.Message);
        Assert.EndsWith("Use 'Gemini' (default), 'Groq' or 'Anthropic'.", ex.Message);
    }

    [Fact]
    public void No_ollama_client_type_remains_in_the_infrastructure_assembly()
    {
        var types = typeof(GeminiAiChatClient).Assembly.GetTypes();

        Assert.DoesNotContain(types, t => t.Name.Contains("Ollama", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Shipped_appsettings_make_gemini_primary_and_groq_the_fallback_without_any_key_or_ollama_section()
    {
        var root = RepositoryRoot();
        var appsettingsPath = Path.Combine(root, "src", "AiReap.Api", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(appsettingsPath, optional: false).Build();
        var groq = configuration.GetSection(GroqOptions.SectionName).Get<GroqOptions>()!;

        Assert.Equal("Gemini", configuration["Ai:Provider"]);
        Assert.Equal("Groq", configuration["Ai:FallbackProvider"]);
        Assert.Equal("openai/gpt-oss-120b", groq.Model);
        Assert.True(string.IsNullOrEmpty(groq.ApiKey));
        Assert.False(configuration.GetSection("Ai:Ollama").Exists());
        Assert.DoesNotContain("11434", File.ReadAllText(appsettingsPath));
    }

    // Real-looking Gemini ("AIza...") or Groq ("gsk_...") keys must never be committed to source or config.
    [Fact]
    public void No_api_keys_in_source_or_configuration_files()
    {
        var root = RepositoryRoot();
        var keyPattern = new System.Text.RegularExpressions.Regex("AIza[0-9A-Za-z_\\-]{35}|gsk_[0-9A-Za-z]{40,}");
        var offenders = new[] { "src", "tests" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".json"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return keyPattern.Matches(text).Any(m => !IsObviousFake(m.Value))
                       || (RealGroqKey is { Length: > 0 } real && text.Contains(real, StringComparison.Ordinal));
            })
            .ToList();

        Assert.Empty(offenders); // file names only - never the matched value
    }

    // Test fakes are low-entropy ("AIzaSyAAAA...", "gsk_AAAA...") or say TEST; real keys are random.
    private static bool IsObviousFake(string key) =>
        key.Contains("TEST", StringComparison.Ordinal) || key[4..].Distinct().Count() < 8;

    private static string? RealGroqKey => Environment.GetEnvironmentVariable("GROQ_API_KEY")?.Trim();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "AiReap.Api", "appsettings.json")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
