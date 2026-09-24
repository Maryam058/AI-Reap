using AiReap.Application.Ai;
using AiReap.Application.Files;
using AiReap.Application.Persistence;
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

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 8;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AiReapDbContext>()
            .AddSignInManager();

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.SectionName));
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        services.Configure<AiResilienceOptions>(configuration.GetSection(AiResilienceOptions.SectionName));

        // Every provider client and the stub are always registered (so the app never fails to
        // start regardless of which provider ends up selected, and switching providers is a config
        // change with no wiring changes). Which one IAiChatClient actually resolves to is decided
        // per-request in ResolveAiChatClient below, once a real IHostEnvironment/IOptions snapshot
        // is available.
        services.AddHttpClient<GeminiAiChatClient>();
        services.AddHttpClient<AnthropicAiChatClient>();
        services.AddHttpClient<OllamaAiChatClient>();
        services.AddSingleton<StubAiChatClient>();
        services.AddScoped<IAiChatClient>(ResolveAiChatClient);
        services.AddScoped<IAiHealthCheckService, AiHealthCheckService>();

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

    // Ai:Provider picks the implementation explicitly. "Gemini" (the default when Provider is
    // unset) is the supported real provider. "Anthropic" and "Ollama" keep their original selection
    // behavior. Any other value is a configuration mistake and fails fast at resolution time rather
    // than silently falling back to something the operator didn't ask for. Every real client is
    // wrapped in ResilientAiChatClient (timeout, retry with backoff, error classification); the
    // stub never is.
    private static IAiChatClient ResolveAiChatClient(IServiceProvider sp)
    {
        var aiOptions = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var provider = string.IsNullOrWhiteSpace(aiOptions.Provider) ? AiOptions.Gemini : aiOptions.Provider.Trim();

        if (string.Equals(provider, AiOptions.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveGemini(sp);
        }

        if (string.Equals(provider, AiOptions.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            return Resilient(sp, sp.GetRequiredService<OllamaAiChatClient>(), AiOptions.Ollama);
        }

        if (!string.Equals(provider, AiOptions.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Ai:Provider is set to '{aiOptions.Provider}', which is not a recognized AI provider. " +
                "Use 'Gemini' (default), 'Anthropic' or 'Ollama'.");
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
            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger("AiReap.Infrastructure.Ai.AiChatClientSelection")
                .LogWarning("Anthropic API key not configured; using development AI stub.");
            return sp.GetRequiredService<StubAiChatClient>();
        }

        throw new InvalidOperationException(
            $"Ai:Anthropic:ApiKey is not configured (or is a placeholder) in the '{environment.EnvironmentName}' " +
            "environment. This environment is not permitted to silently fall back to the development AI stub, " +
            "so real AI analysis cannot run until a valid Anthropic API key is configured.");
    }

    // Key present -> the real Gemini client in every environment. No usable key -> the deterministic
    // stub in Development/Testing only (logged, and recorded as model "stub-canned-model" in the AI
    // audit trail, so it is never mistaken for real output). Anywhere else the Gemini client is
    // still returned and fails each AI call with AiProviderException(NotConfigured), which the API
    // maps to a 503 naming the missing setting - never canned data dressed up as real AI output.
    private static IAiChatClient ResolveGemini(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
        if (!options.HasUsableApiKey)
        {
            var environment = sp.GetRequiredService<IHostEnvironment>();
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AiReap.Infrastructure.Ai.AiChatClientSelection")
                    .LogWarning("Gemini API key not configured; using development AI stub.");
                return sp.GetRequiredService<StubAiChatClient>();
            }
        }

        return Resilient(sp, sp.GetRequiredService<GeminiAiChatClient>(), AiOptions.Gemini);
    }

    private static ResilientAiChatClient Resilient(IServiceProvider sp, IAiChatClient inner, string providerName) =>
        new(inner, providerName,
            sp.GetRequiredService<IOptions<AiResilienceOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientAiChatClient>());
}
