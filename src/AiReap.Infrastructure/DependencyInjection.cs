using AiReap.Application.Common;
using AiReap.Application.Ai;
using AiReap.Application.Files;
using AiReap.Application.Persistence;
using AiReap.Application.Settings;
using AiReap.Infrastructure.Ai;
using AiReap.Infrastructure.Files;
using AiReap.Infrastructure.Identity;
using AiReap.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AiReapDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Default")));

        services.AddScoped<IAiReapDbContext>(sp => sp.GetRequiredService<AiReapDbContext>());
        services.AddScoped<IUserRoleDirectory, UserRoleDirectory>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 8;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AiReapDbContext>()
            .AddSignInManager();

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        // Gemini settings are still bound for embeddings (GeminiEmbeddingClient); Groq settings only for
        // Ai:Groq:MaxBatchedRunParts (AiBatchSettings). Neither provider serves chat requests any more.
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        services.Configure<GroqOptions>(configuration.GetSection(GroqOptions.SectionName));
        services.Configure<AiResilienceOptions>(configuration.GetSection(AiResilienceOptions.SectionName));

        // Claude is the only chat provider. Its client and the stub are always registered (so the app
        // never fails to start whatever the configuration); which one IAiChatClient resolves to is
        // decided per request scope in ResolveAiChatClient. No Gemini or Groq chat client is
        // registered, so nothing can resolve one.
        services.AddHttpClient<AnthropicAiChatClient>();
        services.AddSingleton<StubAiChatClient>();
        services.AddSingleton<AiProviderCooldown>();
        services.AddScoped<IAiChatClient>(ResolveAiChatClient);
        services.AddScoped<IAiRouteInspector, AiRouteInspector>();
        services.AddSingleton<AiReap.Application.Ai.Pipeline.IAiBatchSettings, AiBatchSettings>();
        services.Configure<CopilotOptions>(configuration.GetSection(CopilotOptions.SectionName));
        services.AddSingleton<AiReap.Application.Knowledge.ICopilotSettings, CopilotSettings>();
        services.AddScoped<IAiHealthCheckService, AiHealthCheckService>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        services.Configure<OpenAiOptions>(configuration.GetSection(OpenAiOptions.SectionName));

        switch (SelectEmbeddingProvider(configuration))
        {
            case AiOptions.Gemini:
                services.AddHttpClient<IEmbeddingClient, GeminiEmbeddingClient>();
                break;
            case AiOptions.OpenAI:
                services.AddHttpClient<IEmbeddingClient, OpenAiEmbeddingClient>();
                break;
            default:
                services.AddSingleton<IEmbeddingClient, StubEmbeddingClient>();
                break;
        }

        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

        // Original PDF/DOCX uploads (docs/SOURCE_DOCUMENT_RETENTION.md).
        services.Configure<OriginalDocumentOptions>(configuration.GetSection(OriginalDocumentOptions.SectionName));
        // No RootPath: a directory of its own per database, so two databases on one machine (a dev API,
        // a test host) never share one - see FileSystemOriginalDocumentStore.IsOwnedByAsync.
        services.PostConfigure<OriginalDocumentOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.RootPath))
            {
                options.RootPath = FileSystemOriginalDocumentStore.DefaultRoot(configuration.GetConnectionString("Default"));
            }
        });
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OriginalDocumentOptions>>().Value);
        services.AddSingleton<IOriginalDocumentStore, FileSystemOriginalDocumentStore>();
        services.AddHostedService<OriginalDocumentJanitor>();

        return services;
    }

    // §26 embeddings, independent of the chat provider. Ai:EmbeddingProvider wins when set. Blank keeps
    // the previous automatic choice for every deployment that ran Gemini chat (the shipped default):
    // Gemini when its key is usable, else OpenAI when its key is set, else the deterministic stub.
    // (Before the move to Claude chat, the Gemini branch also required Ai:Provider to be Gemini; a
    // deployment that ran Anthropic chat WITH a Gemini key now gets Gemini embeddings unless it sets
    // Ai:EmbeddingProvider - see docs/CLAUDE_SETUP.md.)
    public static string SelectEmbeddingProvider(IConfiguration configuration)
    {
        var configured = configuration[$"{AiOptions.SectionName}:{nameof(AiOptions.EmbeddingProvider)}"]?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            foreach (var known in new[] { AiOptions.Gemini, AiOptions.OpenAI, AiOptions.Stub })
            {
                if (string.Equals(configured, known, StringComparison.OrdinalIgnoreCase)) return known;
            }

            throw new InvalidOperationException(
                $"Ai:EmbeddingProvider is set to '{configured}', which is not a supported embedding provider. " +
                "Use 'Gemini', 'OpenAI', 'Stub', or leave it empty for automatic selection.");
        }

        var gemini = configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>() ?? new GeminiOptions();
        if (gemini.HasUsableApiKey) return AiOptions.Gemini;
        if (!string.IsNullOrWhiteSpace(configuration[$"{OpenAiOptions.SectionName}:ApiKey"])) return AiOptions.OpenAI;
        return AiOptions.Stub;
    }

    // Ai:Provider must be "Anthropic" (or unset). Gemini and Groq are rejected with an actionable
    // error rather than silently honored or silently replaced, and there is no chat fallback.
    //
    // Real key present -> the real Claude client, in every environment. No usable key -> the
    // deterministic stub in Development/Testing only (loudly logged, and recorded as model
    // "stub-canned-model" in the AI audit trail, so it is never mistaken for real output), so the app
    // and the full pipeline stay runnable offline. Anywhere else the real client is still returned and
    // fails each AI call with AiProviderException(NotConfigured) before sending anything, which the API
    // maps to a 503 naming Ai:Anthropic:ApiKey - never canned data dressed up as real AI output.
    //
    // Settings that would make every request fail or exceed the model's limits (AnthropicOptions.Validate)
    // fail resolution with all problems listed.
    private static IAiChatClient ResolveAiChatClient(IServiceProvider sp)
    {
        var aiOptions = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var provider = string.IsNullOrWhiteSpace(aiOptions.Provider) ? AiOptions.Anthropic : aiOptions.Provider.Trim();

        if (string.Equals(provider, AiOptions.Gemini, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, AiOptions.Groq, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Ai:Provider is set to '{aiOptions.Provider}', but Gemini and Groq are no longer supported as chat providers: " +
                "AI-REAP uses Anthropic Claude only, with no chat fallback. Set Ai:Provider to 'Anthropic' (or remove it) and " +
                "configure Ai:Anthropic:ApiKey. Gemini embeddings are not affected by this setting.");
        }

        if (!string.Equals(provider, AiOptions.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Ai:Provider is set to '{aiOptions.Provider}', which is not a recognized AI provider. Use 'Anthropic'.");
        }

        var fallback = aiOptions.FallbackProvider?.Trim();
        if (!string.IsNullOrEmpty(fallback) && !string.Equals(fallback, "None", StringComparison.OrdinalIgnoreCase))
        {
            SelectionLogger(sp).LogWarning(
                "Ai:FallbackProvider is set to '{FallbackProvider}' but is ignored: AI-REAP has no chat fallback " +
                "(Anthropic Claude only). Remove the setting.", fallback);
        }

        var options = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The Anthropic chat configuration is invalid: " + string.Join(" ", problems));
        }

        if (!options.HasUsableApiKey)
        {
            var environment = sp.GetRequiredService<IHostEnvironment>();
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                SelectionLogger(sp).LogWarning(
                    "Anthropic API key not configured; using development AI stub. Responses are canned test data, NOT real AI " +
                    "output (audit model 'stub-canned-model'). Set Ai:Anthropic:ApiKey to use Claude.");
                return sp.GetRequiredService<StubAiChatClient>();
            }
        }

        return Wrap(sp, sp.GetRequiredService<AnthropicAiChatClient>(), options);
    }

    // The Claude client as it is used: resilience (Ai:Anthropic:TimeoutSeconds / MaxTimeoutAttempts,
    // Ai:Resilience for the rest) and, outside that, the lossless request budget - prompts are never
    // condensed; an over-budget requirement source is split by the caller or refused before sending.
    private static IAiChatClient Wrap(IServiceProvider sp, AnthropicAiChatClient client, AnthropicOptions options)
    {
        var shared = sp.GetRequiredService<IOptions<AiResilienceOptions>>().Value;
        var resilient = Resilient(sp, client, AiOptions.Anthropic, shared.WithTimeout(
            options.TimeoutSeconds > 0 ? options.TimeoutSeconds : shared.TimeoutSeconds,
            Math.Max(0, options.MaxTimeoutAttempts)));

        var budgetSetting = $"{AnthropicOptions.SectionName}:{nameof(AnthropicOptions.MaxRequestTokens)}";
        var policy = new RequestBudgetPolicy(
            CondenseOverBudget: false,
            InputBudgetOverride: options.EstimatedInputBudget(PromptBudgetReducer.CharsPerToken),
            CountInputTokens: options.UseTokenCountingApi ? client.CountInputTokensAsync : null,
            CountedInputBudget: options.InputTokenBudget,
            TooLargeAdvice: "Nothing was sent. Split the source into smaller requirement sources, or ask an administrator to " +
                $"review {budgetSetting}.",
            StagedPartSourceTokens: Math.Max(0, options.GenerationPartSourceTokens),
            StagedMaxParts: options.MaxGenerationParts,
            StagedPartSourceSetting: $"{AnthropicOptions.SectionName}:{nameof(AnthropicOptions.GenerationPartSourceTokens)}",
            StagedMaxPartsSetting: $"{AnthropicOptions.SectionName}:{nameof(AnthropicOptions.MaxGenerationParts)}");

        return new RequestBudgetAiChatClient(
            resilient, AiOptions.Anthropic, budgetSetting, options.MaxRequestTokens, options.MaxTokens,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<RequestBudgetAiChatClient>(),
            options.MaxChunkedParts, $"{AnthropicOptions.SectionName}:{nameof(AnthropicOptions.MaxChunkedParts)}", policy);
    }

    private static ILogger SelectionLogger(IServiceProvider sp) =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("AiReap.Infrastructure.Ai.AiChatClientSelection");

    private static ResilientAiChatClient Resilient(
        IServiceProvider sp, IAiChatClient inner, string providerName, AiResilienceOptions? options = null) =>
        new(inner, providerName,
            options ?? sp.GetRequiredService<IOptions<AiResilienceOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientAiChatClient>());
}
