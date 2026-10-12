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
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        services.Configure<AiResilienceOptions>(configuration.GetSection(AiResilienceOptions.SectionName));

        // Ai:Groq:ApiKey (user-secrets / Ai__Groq__ApiKey) wins; the conventional GROQ_API_KEY
        // environment variable is the fallback source, so an existing machine-level key just works.
        services.AddOptions<GroqOptions>()
            .Bind(configuration.GetSection(GroqOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    options.ApiKey = configuration[GroqOptions.ConventionalKeyVariable] ?? string.Empty;
                }
            });

        // Every provider client and the stub are always registered (so the app never fails to
        // start regardless of which provider ends up selected, and switching providers is a config
        // change with no wiring changes). Which one IAiChatClient actually resolves to is decided
        // per-request in ResolveAiChatClient below, once a real IHostEnvironment/IOptions snapshot
        // is available.
        services.AddHttpClient<GeminiAiChatClient>();
        services.AddHttpClient<GroqAiChatClient>();
        services.AddHttpClient<AnthropicAiChatClient>();
        services.AddSingleton<StubAiChatClient>();
        services.AddSingleton<AiProviderCooldown>();
        services.AddScoped<IAiChatClient>(ResolveAiChatClient);
        services.AddScoped<IAiHealthCheckService, AiHealthCheckService>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        services.Configure<OpenAiOptions>(configuration.GetSection(OpenAiOptions.SectionName));

        // §26 embeddings: Gemini (semantic, same key as chat) when it's the configured provider and
        // a key is present; else OpenAI if its key is set; else the deterministic hash stub.
        var geminiOptions = configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>() ?? new GeminiOptions();
        var chatProvider = configuration[$"{AiOptions.SectionName}:Provider"];
        var openAiApiKey = configuration[$"{OpenAiOptions.SectionName}:ApiKey"];
        if (geminiOptions.HasUsableApiKey
            && (string.IsNullOrWhiteSpace(chatProvider) || string.Equals(chatProvider.Trim(), AiOptions.Gemini, StringComparison.OrdinalIgnoreCase)))
        {
            services.AddHttpClient<IEmbeddingClient, GeminiEmbeddingClient>();
        }
        else if (!string.IsNullOrWhiteSpace(openAiApiKey))
        {
            services.AddHttpClient<IEmbeddingClient, OpenAiEmbeddingClient>();
        }
        else
        {
            services.AddSingleton<IEmbeddingClient, StubEmbeddingClient>();
        }

        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

        return services;
    }

    // Ai:Provider picks the primary implementation explicitly. "Gemini" (the default when Provider
    // is unset) is the primary cloud LLM, with Ai:FallbackProvider ("Groq" by default) behind it in
    // FallbackAiChatClient, so an exhausted Gemini quota needs no config change. "Groq" can also be
    // the primary. "Anthropic" keeps its original selection behavior (no fallback). Any other value
    // is a configuration mistake and fails fast at resolution time rather than silently falling
    // back to something the operator didn't ask for. Every real client is wrapped in
    // ResilientAiChatClient (timeout, retry with backoff, error classification); the stub never is.
    private static IAiChatClient ResolveAiChatClient(IServiceProvider sp)
    {
        var aiOptions = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var provider = string.IsNullOrWhiteSpace(aiOptions.Provider) ? AiOptions.Gemini : aiOptions.Provider.Trim();

        if (IsCloudProvider(provider, out var primary))
        {
            return ResolveCloud(sp, aiOptions, primary);
        }

        if (!string.Equals(provider, AiOptions.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Ai:Provider is set to '{aiOptions.Provider}', which is not a recognized AI provider. " +
                "Use 'Gemini' (default), 'Groq' or 'Anthropic'.");
        }

        // Real key present -> always the real client, in every environment. No usable key -> the
        // deterministic stub in Development/Testing only, so the app and the full Analyze pipeline
        // stay runnable with no live API key. In every other environment (Production, Staging, or
        // anything not explicitly recognized as a dev/test environment), a missing or placeholder key
        // must NOT silently produce stub data dressed up as real AI output - it throws instead, so a
        // misconfigured deployment fails loudly (a 500, logged with the real reason) on the first
        // request that needs AI rather than quietly serving canned analysis.
        var options = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        if (options.HasUsableApiKey)
        {
            return Resilient(sp, sp.GetRequiredService<AnthropicAiChatClient>(), AiOptions.Anthropic);
        }

        var environment = sp.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            SelectionLogger(sp).LogWarning("Anthropic API key not configured; using development AI stub.");
            return sp.GetRequiredService<StubAiChatClient>();
        }

        throw new InvalidOperationException(
            $"Ai:Anthropic:ApiKey is not configured (or is a placeholder) in the '{environment.EnvironmentName}' " +
            "environment. This environment is not permitted to silently fall back to the development AI stub, " +
            "so real AI analysis cannot run until a valid Anthropic API key is configured.");
    }

    // Both keys -> primary with automatic fallback. One key -> that provider alone (logged when it
    // isn't the primary). No key -> the deterministic stub in Development/Testing only (loudly
    // logged, and recorded as model "stub-canned-model" in the AI audit trail, so it is never
    // mistaken for real output); the stub is never used while any real provider is configured.
    // Anywhere else the primary client is still returned and fails each AI call with
    // AiProviderException(NotConfigured), which the API maps to a 503 naming the missing setting -
    // never canned data dressed up as real AI output.
    private static IAiChatClient ResolveCloud(IServiceProvider sp, AiOptions aiOptions, string primaryName)
    {
        var fallbackName = ResolveFallbackName(aiOptions, primaryName);
        var primary = ConfiguredCloudClient(sp, primaryName);
        var fallback = fallbackName is null ? null : ConfiguredCloudClient(sp, fallbackName);

        if (primary is not null && fallback is not null)
        {
            var resilience = sp.GetRequiredService<IOptions<AiResilienceOptions>>().Value;
            return new FallbackAiChatClient(
                Wrap(sp, primary, primaryName, resilience.WithoutRateLimitRetry()), primaryName,
                Wrap(sp, fallback, fallbackName!), fallbackName!,
                sp.GetRequiredService<AiProviderCooldown>(),
                TimeSpan.FromSeconds(Math.Max(0, aiOptions.PrimaryCooldownSeconds)),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<FallbackAiChatClient>());
        }

        if (primary is not null)
        {
            return Wrap(sp, primary, primaryName);
        }

        if (fallback is not null)
        {
            SelectionLogger(sp).LogWarning(
                "{Primary} API key not configured; using {Fallback} only (no automatic fallback).", primaryName, fallbackName);
            return Wrap(sp, fallback, fallbackName!);
        }

        var environment = sp.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            SelectionLogger(sp).LogWarning(
                "No AI provider API key configured ({Settings}); using the DEVELOPMENT AI STUB. " +
                "Responses are canned test data, NOT real AI output (audit model 'stub-canned-model').",
                fallbackName is null ? $"Ai:{primaryName}:ApiKey" : $"Ai:{primaryName}:ApiKey, Ai:{fallbackName}:ApiKey");
            return sp.GetRequiredService<StubAiChatClient>();
        }

        return Wrap(sp, CloudClient(sp, primaryName), primaryName);
    }

    // A cloud client as it is used: resilience around it, plus - for Groq, whose free tier caps the
    // size of one request - the request budget outside that, so a prompt is condensed once and every
    // retry reuses it. Gemini gets no budget: its requests are sent exactly as built.
    private static IAiChatClient Wrap(IServiceProvider sp, IAiChatClient client, string name, AiResilienceOptions? resilience = null)
    {
        if (name != AiOptions.Groq)
        {
            return Resilient(sp, client, name, resilience);
        }

        var groq = sp.GetRequiredService<IOptions<GroqOptions>>().Value;
        var resilient = Resilient(sp, client, name,
            (resilience ?? sp.GetRequiredService<IOptions<AiResilienceOptions>>().Value).WithRateLimitAttempts(groq.MaxRateLimitAttempts));
        return new RequestBudgetAiChatClient(
            resilient, name, $"{GroqOptions.SectionName}:{nameof(GroqOptions.MaxRequestTokens)}",
            groq.MaxRequestTokens, groq.ReservedOutputTokens,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<RequestBudgetAiChatClient>());
    }

    private static bool IsCloudProvider(string name, out string canonical)
    {
        canonical = string.Equals(name, AiOptions.Gemini, StringComparison.OrdinalIgnoreCase) ? AiOptions.Gemini
            : string.Equals(name, AiOptions.Groq, StringComparison.OrdinalIgnoreCase) ? AiOptions.Groq
            : string.Empty;
        return canonical.Length > 0;
    }

    private static string? ResolveFallbackName(AiOptions aiOptions, string primaryName)
    {
        var name = aiOptions.FallbackProvider?.Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!IsCloudProvider(name, out var fallbackName))
        {
            throw new InvalidOperationException(
                $"Ai:FallbackProvider is set to '{aiOptions.FallbackProvider}', which is not a supported fallback provider. " +
                "Use 'Groq' (default), 'Gemini' or 'None'.");
        }

        return fallbackName == primaryName ? null : fallbackName;
    }

    private static IAiChatClient? ConfiguredCloudClient(IServiceProvider sp, string name)
    {
        var hasKey = name == AiOptions.Groq
            ? sp.GetRequiredService<IOptions<GroqOptions>>().Value.HasUsableApiKey
            : sp.GetRequiredService<IOptions<GeminiOptions>>().Value.HasUsableApiKey;
        return hasKey ? CloudClient(sp, name) : null;
    }

    private static IAiChatClient CloudClient(IServiceProvider sp, string name) => name == AiOptions.Groq
        ? sp.GetRequiredService<GroqAiChatClient>()
        : sp.GetRequiredService<GeminiAiChatClient>();

    private static ILogger SelectionLogger(IServiceProvider sp) =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("AiReap.Infrastructure.Ai.AiChatClientSelection");

    private static ResilientAiChatClient Resilient(
        IServiceProvider sp, IAiChatClient inner, string providerName, AiResilienceOptions? options = null) =>
        new(inner, providerName,
            options ?? sp.GetRequiredService<IOptions<AiResilienceOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientAiChatClient>());
}
