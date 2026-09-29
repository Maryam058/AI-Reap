using AiReap.Application.Ai;

namespace AiReap.Application.Settings;

// Administrator-facing view of the AI runtime configuration and global operational settings
// (GET /api/settings). Every value is mapped field by field from the bound options - options
// objects are never serialized, so API keys, tokens and connection strings cannot reach the
// client. All values are read-only: they come from server configuration (appsettings,
// user-secrets, environment variables) and take effect on restart.
public record SystemSettingsResponse(
    AiConfigurationDto Ai,
    RuntimeSettingsDto Runtime,
    string CredentialsNote);

// Status is exactly what GET /api/ai/status returns (the same IAiHealthCheckService call).
public record AiConfigurationDto(
    AiProviderStatus Status,
    bool FallbackEnabled,
    DateTimeOffset? PrimaryCooldownUntil,
    bool UsingDevelopmentStub,
    DateTimeOffset CheckedAt);

public record RuntimeSettingsDto(
    string Environment,
    int PrimaryCooldownSeconds,
    int RequestTimeoutSeconds,
    int MaxAttempts);

public interface ISystemSettingsService
{
    Task<SystemSettingsResponse> GetAsync(CancellationToken cancellationToken = default);
}
