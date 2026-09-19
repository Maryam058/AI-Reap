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

        var anthropicApiKey = configuration[$"{AnthropicOptions.SectionName}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(anthropicApiKey))
        {
            services.AddHttpClient<IAiChatClient, AnthropicAiChatClient>();
        }
        else
        {
            services.AddSingleton<IAiChatClient, StubAiChatClient>();
        }

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
}
