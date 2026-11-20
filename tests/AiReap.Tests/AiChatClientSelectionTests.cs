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

// Covers AddInfrastructure's choice of IAiChatClient and IEmbeddingClient (AiReap.Infrastructure/
// DependencyInjection.cs) through the real, public AddInfrastructure entry point, the same way
// Program.cs calls it. Policy: Anthropic Claude is the only chat provider, with no chat fallback;
// Gemini and Groq are rejected as chat providers; embeddings are chosen independently of chat.
// Offline: resolving clients never sends a request (no test here calls CompleteAsync on a real
// transport except where a missing key guarantees nothing is sent).
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
    internal sealed class CapturingLoggerProvider : ILoggerProvider
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
    internal const string PlausibleRealKey = "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // A syntactically plausible Gemini key ("AIza" + 35 chars) that is not a real key.
    private const string PlausibleGeminiKey = "AIzaSyAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // A syntactically plausible Groq key ("gsk_" + 52 chars) that is not a real key.
    private const string PlausibleGroqKey = "gsk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // aiProvider null = Ai:Provider not configured at all; anthropicModel null = Ai:Anthropic:Model not configured.
    internal static (ServiceProvider Provider, CapturingLoggerProvider Logs) BuildProvider(
        string? apiKey, string environmentName, string? aiProvider = "Anthropic", string? geminiKey = null,
        string? groqKey = null, IDictionary<string, string?>? extra = null, string? anthropicModel = "claude-sonnet-5",
        Action<IServiceCollection>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=unused;Database=unused;Trusted_Connection=True",
            ["Ai:Anthropic:ApiKey"] = apiKey,
            ["Ai:Gemini:ApiKey"] = geminiKey,
            ["Ai:Groq:ApiKey"] = groqKey,
        };
        if (aiProvider is not null) settings["Ai:Provider"] = aiProvider;
        if (anthropicModel is not null) settings["Ai:Anthropic:Model"] = anthropicModel;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .AddInMemoryCollection(extra ?? new Dictionary<string, string?>())
            .Build();

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment { EnvironmentName = environmentName });
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddInfrastructure(configuration);
        configure?.Invoke(services);

        return (services.BuildServiceProvider(), logs);
    }

    // Every client in the wrapper chain, outermost first.
    internal static List<IAiChatClient> Chain(IAiChatClient client)
    {
        var chain = new List<IAiChatClient>();
        for (IAiChatClient? current = client; current is not null;)
        {
            chain.Add(current);
            current = current switch
            {
                RequestBudgetAiChatClient budget => budget.Inner,
                ResilientAiChatClient resilient => resilient.Inner,
                _ => null
            };
        }

        return chain;
    }

    private static AnthropicAiChatClient AssertClaudeOnly(IAiChatClient client)
    {
        var chain = Chain(client);
        Assert.DoesNotContain(chain, c => c is GeminiAiChatClient or GroqAiChatClient or FallbackAiChatClient or StubAiChatClient);
        var budget = Assert.IsType<RequestBudgetAiChatClient>(chain[0]);
        var resilient = Assert.IsType<ResilientAiChatClient>(budget.Inner);
        Assert.Equal("Anthropic", resilient.ProviderName);
        return Assert.IsType<AnthropicAiChatClient>(resilient.Inner);
    }

    // ---- Claude is the chat provider -----------------------------------------------------------

    [Theory]
    [InlineData(null)]          // Ai:Provider unset -> Anthropic is the default
    [InlineData("Anthropic")]
    [InlineData("anthropic")]
    [InlineData("  ")]
    public async Task Anthropic_is_the_chat_provider_by_default_and_when_named(string? aiProvider)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, aiProvider);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        AssertClaudeOnly(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Claude_is_used_even_when_gemini_and_groq_keys_are_configured(string environmentName)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, environmentName, geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        AssertClaudeOnly(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
    }

    [Fact]
    public async Task No_gemini_or_groq_chat_client_is_registered_at_all()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey);
        await using var _ = provider;

        Assert.Null(provider.GetService<GeminiAiChatClient>());
        Assert.Null(provider.GetService<GroqAiChatClient>());
        Assert.Null(provider.GetService<FallbackAiChatClient>());
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("gemini")]
    [InlineData("Groq")]
    public async Task Gemini_or_groq_as_chat_provider_fails_with_an_actionable_error_instead_of_being_used(string aiProvider)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Development, aiProvider, geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());

        Assert.Contains("Ai:Provider", ex.Message);
        Assert.Contains("no longer supported as chat providers", ex.Message);
        Assert.Contains("Anthropic", ex.Message);
        Assert.Contains("Ai:Anthropic:ApiKey", ex.Message);
    }

    [Theory]
    [InlineData("SomeOtherVendor")]
    [InlineData("Ollama")]
    public async Task An_unrecognized_provider_fails_with_a_clear_error(string aiProvider)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Development, aiProvider);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.Contains("Ai:Provider", ex.Message);
        Assert.Contains(aiProvider, ex.Message);
        Assert.EndsWith("Use 'Anthropic'.", ex.Message);
    }

    [Theory]
    [InlineData("Groq")]
    [InlineData("Gemini")]
    public async Task A_leftover_fallback_provider_is_ignored_with_a_warning_and_never_routes_a_request(string fallback)
    {
        var (provider, logs) = BuildProvider(PlausibleRealKey, Environments.Production, geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey,
            extra: new Dictionary<string, string?> { ["Ai:FallbackProvider"] = fallback });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        AssertClaudeOnly(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.Contains(logs.Messages, m => m.Contains("Ai:FallbackProvider") && m.Contains("ignored"));
    }

    // ---- model -------------------------------------------------------------------------------

    [Fact]
    public async Task An_explicitly_configured_anthropic_model_is_preserved()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, anthropicModel: "claude-sonnet-5");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        Assert.Equal("claude-sonnet-5", client.ModelName);
        Assert.Equal("claude-sonnet-5", AssertClaudeOnly(client).ModelName);
    }

    [Fact]
    public async Task Without_a_configured_model_claude_sonnet_5_5_is_used()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, anthropicModel: null);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.Equal("claude-sonnet-5-5", scope.ServiceProvider.GetRequiredService<IAiChatClient>().ModelName);
    }

    // ---- limits ------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_runs_under_a_lossless_application_budget_and_its_own_timeout()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var budget = Assert.IsType<RequestBudgetAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        var resilience = Assert.IsType<ResilientAiChatClient>(budget.Inner).Options;

        Assert.False(budget.CondensesOverBudget);                      // never drops passages
        Assert.Equal(200_000, budget.MaxRequestTokens);                 // Ai:Anthropic:MaxRequestTokens default
        // (200,000 - 16,000 output) Claude tokens, scaled from 2.5 to the estimator's 3 chars/token.
        Assert.Equal((int)Math.Floor(184_000 * (2.5 / 3.0)), budget.InputBudget);
        Assert.Equal(8, budget.MaxChunkedParts);
        Assert.Equal("Ai:Anthropic:MaxRequestTokens", budget.BudgetSetting);
        Assert.Equal("Ai:Anthropic:MaxChunkedParts", budget.MaxPartsSetting);
        Assert.Null(budget.Policy.CountInputTokens);                    // count_tokens is opt-in
        Assert.Equal((600, 1), (resilience.TimeoutSeconds, resilience.MaxTimeoutAttempts));
    }

    [Fact]
    public async Task Staged_generation_limits_are_advertised_from_the_anthropic_settings()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production,
            extra: new Dictionary<string, string?> { ["Ai:Anthropic:GenerationPartSourceTokens"] = "9000", ["Ai:Anthropic:MaxGenerationParts"] = "12" });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var staging = Assert.IsAssignableFrom<IStagedOutputAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>()).OutputStaging!;

        Assert.Equal(("Anthropic", 9000, 12), (staging.Provider, staging.MaxPartSourceTokens, staging.MaxParts));
        Assert.Equal("Ai:Anthropic:GenerationPartSourceTokens", staging.PartSourceSetting);
        Assert.Equal("Ai:Anthropic:MaxGenerationParts", staging.MaxPartsSetting);
        Assert.Equal((int)Math.Floor(184_000 * (2.5 / 3.0)), staging.InputBudget);
    }

    [Fact]
    public async Task Staged_generation_can_be_turned_off_and_then_a_source_goes_out_in_one_response()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production,
            extra: new Dictionary<string, string?> { ["Ai:Anthropic:GenerationPartSourceTokens"] = "0" });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.Null(Assert.IsAssignableFrom<IStagedOutputAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>()).OutputStaging);
    }

    [Theory]
    [InlineData("Ai:Anthropic:GenerationPartSourceTokens", "100", "GenerationPartSourceTokens")]
    [InlineData("Ai:Anthropic:MaxGenerationParts", "0", "MaxGenerationParts")]
    public async Task Invalid_staging_settings_fail_with_the_setting_named(string key, string value, string expected)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, extra: new Dictionary<string, string?> { [key] = value });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Token_counting_is_wired_only_when_opted_in()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production,
            extra: new Dictionary<string, string?> { ["Ai:Anthropic:UseTokenCountingApi"] = "true" });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var budget = Assert.IsType<RequestBudgetAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.NotNull(budget.Policy.CountInputTokens);
        Assert.Equal(184_000, budget.Policy.CountedInputBudget);
    }

    [Theory]
    [InlineData("Ai:Anthropic:MaxRequestTokens", "2000000", "context window")]
    [InlineData("Ai:Anthropic:MaxTokens", "200000", "MaxTokens")]
    [InlineData("Ai:Anthropic:Effort", "extreme", "Effort")]
    [InlineData("Ai:Anthropic:Model", " ", "Model")]
    public async Task Invalid_anthropic_settings_fail_with_every_problem_named(string key, string value, string expected)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Development, extra: new Dictionary<string, string?> { [key] = value });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IAiChatClient>());
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain(PlausibleRealKey, ex.Message, StringComparison.Ordinal);
    }

    // ---- missing key ------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sk-ant-...")]
    public async Task Development_uses_the_loudly_logged_stub_when_no_usable_key_is_configured(string? apiKey)
    {
        var (provider, logs) = BuildProvider(apiKey, Environments.Development, geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();

        // Never Gemini or Groq, even though their keys are present.
        Assert.IsType<StubAiChatClient>(client);
        Assert.Equal("stub-canned-model", client.ModelName);
        Assert.Contains(logs.Messages, m => m.Contains("Anthropic API key not configured; using development AI stub."));
    }

    [Fact]
    public async Task The_testing_environment_also_uses_the_stub_when_no_usable_key_is_configured()
    {
        var (provider, _) = BuildProvider(apiKey: null, "Testing");
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType<StubAiChatClient>(scope.ServiceProvider.GetRequiredService<IAiChatClient>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-ant-...")]
    public async Task Production_without_a_usable_key_never_uses_the_stub_or_another_provider_and_fails_calls_as_not_configured(string? apiKey)
    {
        var (provider, _) = BuildProvider(apiKey, Environments.Production, geminiKey: PlausibleGeminiKey, groqKey: PlausibleGroqKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IAiChatClient>();
        AssertClaudeOnly(client);

        // Safe offline: with no usable key the client throws before building any HTTP request.
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync("system", "user"));
        Assert.Equal(AiFailureKind.NotConfigured, ex.Kind);
        Assert.Equal("Anthropic", ex.Provider);
        Assert.Contains("Ai:Anthropic:ApiKey", ex.Message);
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

    // ---- health check -------------------------------------------------------------------------

    [Fact]
    public async Task Health_check_reports_claude_without_calling_any_provider()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, anthropicModel: null);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IAiHealthCheckService>().CheckAsync();

        Assert.Equal("Anthropic", status.Provider);
        Assert.True(status.Configured);
        Assert.Equal("claude-sonnet-5-5", status.Model);
        Assert.Null(status.Fallback);
    }

    [Fact]
    public async Task Health_check_reports_a_gemini_chat_setting_as_not_configured_with_the_fix()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, aiProvider: "Gemini", geminiKey: PlausibleGeminiKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IAiHealthCheckService>().CheckAsync();

        Assert.False(status.Configured);
        Assert.Contains("only supported chat provider", status.Detail);
    }

    // ---- embeddings: independent of the chat provider ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("Anthropic")]
    public async Task Gemini_embeddings_stay_selected_with_the_same_model_name_when_chat_is_claude(string? aiProvider)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, aiProvider, geminiKey: PlausibleGeminiKey);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

        Assert.IsType<GeminiEmbeddingClient>(embeddings);
        // DocumentChunk.EmbeddingModel values written before the move to Claude keep matching.
        Assert.Equal("gemini-embedding-001@768", embeddings.ModelName);
    }

    [Fact]
    public async Task Without_any_embedding_key_the_deterministic_stub_is_used()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production);
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType<StubEmbeddingClient>(scope.ServiceProvider.GetRequiredService<IEmbeddingClient>());
    }

    [Fact]
    public async Task OpenAI_embeddings_are_used_when_only_an_openai_key_is_configured()
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production,
            extra: new Dictionary<string, string?> { ["Ai:OpenAI:ApiKey"] = "openai-test-key", ["Ai:OpenAI:Model"] = "text-embedding-3-small" });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
        Assert.IsType<OpenAiEmbeddingClient>(embeddings);
        Assert.Equal("text-embedding-3-small", embeddings.ModelName);
    }

    [Theory]
    [InlineData("OpenAI", typeof(OpenAiEmbeddingClient))]
    [InlineData("stub", typeof(StubEmbeddingClient))]
    [InlineData("Gemini", typeof(GeminiEmbeddingClient))]
    public async Task An_explicit_embedding_provider_wins_over_automatic_selection(string embeddingProvider, Type expected)
    {
        var (provider, _) = BuildProvider(PlausibleRealKey, Environments.Production, geminiKey: PlausibleGeminiKey,
            extra: new Dictionary<string, string?> { ["Ai:EmbeddingProvider"] = embeddingProvider, ["Ai:OpenAI:ApiKey"] = "openai-test-key" });
        await using var _ = provider;
        using var scope = provider.CreateScope();

        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IEmbeddingClient>());
    }

    [Fact]
    public void An_unknown_embedding_provider_fails_at_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildProvider(PlausibleRealKey, Environments.Production, extra: new Dictionary<string, string?> { ["Ai:EmbeddingProvider"] = "Voyage" }));
        Assert.Contains("Ai:EmbeddingProvider", ex.Message);
    }

    // ---- shipped configuration ---------------------------------------------------------------

    [Fact]
    public void Shipped_appsettings_make_claude_sonnet_5_5_the_only_chat_provider_without_any_committed_key()
    {
        var root = RepositoryRoot();
        var appsettingsPath = Path.Combine(root, "src", "AiReap.Api", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(appsettingsPath, optional: false).Build();
        var anthropic = configuration.GetSection(AnthropicOptions.SectionName).Get<AnthropicOptions>()!;
        var gemini = configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>()!;

        Assert.Equal("Anthropic", configuration["Ai:Provider"]);
        Assert.True(string.IsNullOrEmpty(configuration["Ai:FallbackProvider"]));
        Assert.Equal("claude-sonnet-5-5", anthropic.Model);
        Assert.Empty(anthropic.Validate());
        Assert.True(string.IsNullOrEmpty(anthropic.ApiKey));
        Assert.Equal(16000, anthropic.MaxTokens);
        Assert.Equal(200_000, anthropic.MaxRequestTokens);
        Assert.False(anthropic.UseTokenCountingApi);
        Assert.Equal((6000, 24), (anthropic.GenerationPartSourceTokens, anthropic.MaxGenerationParts));
        // Embedding model unchanged (re-embedding is never triggered by this configuration).
        Assert.Equal(("gemini-embedding-001", 768), (gemini.EmbeddingModel, gemini.EmbeddingDimensions));
        Assert.True(string.IsNullOrEmpty(gemini.ApiKey));
        Assert.True(string.IsNullOrEmpty(configuration["Ai:EmbeddingProvider"]));
        Assert.False(configuration.GetSection("Ai:Groq").Exists());
        Assert.False(configuration.GetSection("Ai:Ollama").Exists());
    }

    [Fact]
    public void No_ollama_client_type_remains_in_the_infrastructure_assembly()
    {
        var types = typeof(GeminiAiChatClient).Assembly.GetTypes();

        Assert.DoesNotContain(types, t => t.Name.Contains("Ollama", StringComparison.OrdinalIgnoreCase));
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

    internal static string RepositoryRoot()
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
