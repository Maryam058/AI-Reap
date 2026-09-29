using AiReap.Application.Ai;
using AiReap.Application.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Composes the existing /api/ai/status health check with the non-secret runtime options behind
// it. Provider and model names come from that check rather than being re-derived here, so the two
// endpoints can't disagree. Only plain values are copied into the response - never an options object.
public class SystemSettingsService : ISystemSettingsService
{
    public const string CredentialsNote =
        "Provider credentials are managed through secure server configuration and are never displayed in the application.";

    private readonly IAiHealthCheckService _healthCheck;
    private readonly AiProviderCooldown _cooldown;
    private readonly AiOptions _aiOptions;
    private readonly AiResilienceOptions _resilienceOptions;
    private readonly IHostEnvironment _environment;

    public SystemSettingsService(
        IAiHealthCheckService healthCheck,
        AiProviderCooldown cooldown,
        IOptions<AiOptions> aiOptions,
        IOptions<AiResilienceOptions> resilienceOptions,
        IHostEnvironment environment)
    {
        _healthCheck = healthCheck;
        _cooldown = cooldown;
        _aiOptions = aiOptions.Value;
        _resilienceOptions = resilienceOptions.Value;
        _environment = environment;
    }

    public async Task<SystemSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var status = await _healthCheck.CheckAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // Mirrors DependencyInjection.ResolveAiChatClient: with no usable key for either provider,
        // Development/Testing serve the canned stub instead of real AI.
        var usingStub = (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
            && !status.Configured
            && status.Fallback?.Configured != true;

        var ai = new AiConfigurationDto(
            status,
            FallbackEnabled: status.Fallback is not null,
            PrimaryCooldownUntil: _cooldown.IsCoolingDown(status.Provider, now, out var until) ? until : null,
            UsingDevelopmentStub: usingStub,
            CheckedAt: now);

        var runtime = new RuntimeSettingsDto(
            _environment.EnvironmentName,
            _aiOptions.PrimaryCooldownSeconds,
            _resilienceOptions.TimeoutSeconds,
            _resilienceOptions.MaxAttempts);

        return new SystemSettingsResponse(ai, runtime, CredentialsNote);
    }
}
