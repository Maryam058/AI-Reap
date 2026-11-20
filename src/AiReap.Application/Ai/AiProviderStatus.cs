namespace AiReap.Application.Ai;

// Read-only diagnostic snapshot of which AI provider is configured and whether it's usable
// right now: whether the key is accepted, the configured model exists, and which provider is
// active - plus the same for the automatic fallback provider, when one is configured. Never
// triggers an actual generation call.
public record AiProviderStatus(
    string Provider,
    bool Configured,
    bool ModelAvailable,
    string Model,
    string? Detail,
    AiProviderStatus? Fallback = null);

public interface IAiHealthCheckService
{
    Task<AiProviderStatus> CheckAsync(CancellationToken cancellationToken = default);
}
