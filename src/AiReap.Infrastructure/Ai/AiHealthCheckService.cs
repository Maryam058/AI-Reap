using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Backs the /api/ai/status diagnostic endpoint for the only chat provider, Anthropic Claude. Never
// sends a request to any provider (a generation would spend money on every health check): it reports
// whether a usable key is configured and whether the Anthropic settings are valid. A configuration
// still naming Gemini or Groq as the chat provider is reported as not configured, with the fix.
// Whether the key is actually accepted and the model is available is only known on the first real
// call; ModelAvailable therefore means "configured", not "verified".
public class AiHealthCheckService : IAiHealthCheckService
{
    private readonly AiOptions _aiOptions;
    private readonly AnthropicOptions _anthropicOptions;

    public AiHealthCheckService(IOptions<AiOptions> aiOptions, IOptions<AnthropicOptions> anthropicOptions)
    {
        _aiOptions = aiOptions.Value;
        _anthropicOptions = anthropicOptions.Value;
    }

    public Task<AiProviderStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var provider = string.IsNullOrWhiteSpace(_aiOptions.Provider) ? AiOptions.Anthropic : _aiOptions.Provider.Trim();
        if (!string.Equals(provider, AiOptions.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new AiProviderStatus(provider, Configured: false, ModelAvailable: false, _anthropicOptions.Model,
                $"Ai:Provider is '{provider}', but Anthropic Claude is the only supported chat provider. " +
                "Set Ai:Provider to 'Anthropic' (or remove it) and configure Ai:Anthropic:ApiKey."));
        }

        var problems = _anthropicOptions.Validate();
        if (problems.Count > 0)
        {
            return Task.FromResult(new AiProviderStatus(AiOptions.Anthropic, Configured: false, ModelAvailable: false,
                _anthropicOptions.Model, string.Join(" ", problems)));
        }

        var hasKey = _anthropicOptions.HasUsableApiKey;
        return Task.FromResult(new AiProviderStatus(
            AiOptions.Anthropic,
            Configured: hasKey,
            ModelAvailable: hasKey,
            Model: _anthropicOptions.Model,
            Detail: hasKey
                ? "Key configured; not verified against the Anthropic API (no request is sent by this check)."
                : "Ai:Anthropic:ApiKey is not configured (or is a placeholder). With no key, Development uses the canned stub instead of real AI."));
    }
}
