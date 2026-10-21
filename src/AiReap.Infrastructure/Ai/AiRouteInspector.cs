using AiReap.Application.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace AiReap.Infrastructure.Ai;

// Reads the request limits off the IAiChatClient this request scope actually resolves - the same
// FallbackAiChatClient / RequestBudgetAiChatClient / ResilientAiChatClient chain the pipeline calls
// (DependencyInjection.ResolveAiChatClient) - so the dry-run AI plan cannot disagree with the
// limits a real call enforces. Reads properties only: never sends a request to any provider.
public sealed class AiRouteInspector : IAiRouteInspector
{
    private readonly IServiceProvider _services;

    public AiRouteInspector(IServiceProvider services)
    {
        _services = services;
    }

    public AiRouteSnapshot Inspect()
    {
        IAiChatClient client;
        try
        {
            client = _services.GetRequiredService<IAiChatClient>();
        }
        catch (InvalidOperationException ex)
        {
            return new AiRouteSnapshot(null, null, null, ex.Message);
        }

        return Inspect(client);
    }

    public static AiRouteSnapshot Inspect(IAiChatClient client) => client is FallbackAiChatClient fallback
        ? new AiRouteSnapshot(
            Describe(fallback.Primary, fallback.PrimaryName),
            Describe(fallback.Fallback, fallback.FallbackName),
            fallback.PrimaryCooldownUntil)
        : new AiRouteSnapshot(Describe(client, null), null, null);

    private static AiProviderLimits Describe(IAiChatClient client, string? name)
    {
        switch (client)
        {
            case RequestBudgetAiChatClient budget:
            {
                var inner = Describe(budget.Inner, budget.ProviderName);
                return budget.MaxRequestTokens <= 0
                    ? inner
                    : inner with
                    {
                        InputBudget = budget.InputBudget,
                        MaxRequestTokens = budget.MaxRequestTokens,
                        BudgetSetting = budget.BudgetSetting,
                        MaxParts = budget.MaxChunkedParts,
                        MaxPartsSetting = budget.MaxPartsSetting
                    };
            }

            case ResilientAiChatClient resilient:
            {
                var inner = Describe(resilient.Inner, name ?? resilient.ProviderName);
                return inner with { TimeoutSeconds = Math.Max(1, resilient.Options.TimeoutSeconds) };
            }

            case GeminiAiChatClient gemini:
                return Known(name ?? AiOptions.Gemini, gemini.ModelName) with
                {
                    MaxOutputTokens = gemini.Options.MaxOutputTokens,
                    MaxOutputTokensSetting = $"{GeminiOptions.SectionName}:{nameof(GeminiOptions.MaxOutputTokens)}",
                    LargeGenerationWarningTokens = gemini.Options.LargeGenerationWarningTokens > 0
                        ? gemini.Options.LargeGenerationWarningTokens
                        : null
                };

            case StubAiChatClient stub:
                return Known("Development stub", stub.ModelName) with { IsStub = true };

            case IUncondensedAiChatClient:
                // A client that may impose a budget this inspector cannot read.
                return Known(name ?? client.ModelName, client.ModelName) with { LimitsKnown = false };

            default:
                // No budget capability: CompleteUncondensedAsync sends the prompt whole.
                return Known(name ?? client.ModelName, client.ModelName);
        }
    }

    private static AiProviderLimits Known(string provider, string model) =>
        new(provider, model, IsStub: false, LimitsKnown: true, InputBudget: null, MaxRequestTokens: null, BudgetSetting: null,
            MaxParts: 0, MaxPartsSetting: null, MaxOutputTokens: null, MaxOutputTokensSetting: null, TimeoutSeconds: null,
            LargeGenerationWarningTokens: null);
}
