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

        // The Anthropic client, the Ollama client, and the stub are all always registered (so
        // the app never fails to start regardless of which provider ends up selected, and
        // switching providers is a config change with no wiring changes - requirement: keep the
        // real Anthropic implementation reachable with no architectural changes). Which one
        // IAiChatClient actually resolves to is decided per-request in ResolveAiChatClient below,
        // once a real IHostEnvironment/IOptions snapshot is available.
        services.AddHttpClient<AnthropicAiChatClient>();
        services.AddHttpClient<OllamaAiChatClient>();
        services.AddSingleton<StubAiChatClient>();
        services.AddScoped<IAiChatClient>(ResolveAiChatClient);
        services.AddScoped<IAiHealthCheckService, AiHealthCheckService>();

        services.Configure<OpenAiOptions>(configuration.GetSection(OpenAiOptions.SectionName));

        var openAiApiKey = configuration[$"{OpenAiOptions.SectionName}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(openAiApiKey))
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

    // Ai:Provider picks the implementation explicitly: "Ollama" always resolves to the local
    // Ollama client (no API key involved, so no environment-based fallback logic applies - it's
    // either reachable or OllamaAiChatClient throws a clear error on the first call). "Anthropic"
    // (the default when Provider is unset) preserves the original behavior below unchanged. Any
    // other value is a configuration mistake and fails fast at resolution time rather than
    // silently falling back to something the operator didn't ask for.
    private static IAiChatClient ResolveAiChatClient(IServiceProvider sp)
    {
        var aiOptions = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var provider = string.IsNullOrWhiteSpace(aiOptions.Provider) ? "Anthropic" : aiOptions.Provider.Trim();

        if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return sp.GetRequiredService<OllamaAiChatClient>();
        }

        if (!string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Ai:Provider is set to '{aiOptions.Provider}', which is not a recognized AI provider. " +
                "Use 'Anthropic' or 'Ollama'.");
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
            return sp.GetRequiredService<AnthropicAiChatClient>();
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
}
