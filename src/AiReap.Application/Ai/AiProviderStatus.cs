namespace AiReap.Application.Ai;

// Read-only diagnostic snapshot of which AI provider is configured and whether it's usable
// right now (§ Step 8: "determine whether Ollama is reachable, the configured model exists,
// and which provider is active"). Never triggers an actual generation call.
public record AiProviderStatus(
    string Provider,
    bool Configured,
    bool ModelAvailable,
    string Model,
    string? Detail);

public interface IAiHealthCheckService
{
    Task<AiProviderStatus> CheckAsync(CancellationToken cancellationToken = default);
}
