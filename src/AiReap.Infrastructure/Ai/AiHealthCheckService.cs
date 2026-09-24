using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Backs the /api/ai/status diagnostic endpoint. Deliberately never calls the Anthropic API
// itself (that would spend real money on every health check) - the Anthropic branch only
// reports whether a usable key is configured. The Ollama branch calls Ollama's own local
// /api/tags, which is free, to check both reachability and whether the configured model is
// actually installed.
public class AiHealthCheckService : IAiHealthCheckService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiOptions _aiOptions;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly OllamaOptions _ollamaOptions;

    public AiHealthCheckService(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> aiOptions,
        IOptions<AnthropicOptions> anthropicOptions,
        IOptions<OllamaOptions> ollamaOptions)
    {
        _httpClientFactory = httpClientFactory;
        _aiOptions = aiOptions.Value;
        _anthropicOptions = anthropicOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
    }

    public Task<AiProviderStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var provider = string.IsNullOrWhiteSpace(_aiOptions.Provider) ? "Anthropic" : _aiOptions.Provider.Trim();

        return string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)
            ? CheckOllamaAsync(cancellationToken)
            : Task.FromResult(CheckAnthropic());
    }

    private AiProviderStatus CheckAnthropic() => new(
        "Anthropic",
        Configured: _anthropicOptions.HasUsableApiKey,
        ModelAvailable: _anthropicOptions.HasUsableApiKey,
        Model: _anthropicOptions.Model,
        Detail: _anthropicOptions.HasUsableApiKey
            ? null
            : "Ai:Anthropic:ApiKey is not configured (or is a placeholder).");

    private async Task<AiProviderStatus> CheckOllamaAsync(CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(_ollamaOptions.BaseUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        try
        {
            var tags = await httpClient.GetFromJsonAsync<OllamaTagsResponse>("api/tags", cancellationToken);
            var installedModels = tags?.Models?.Select(m => m.Name).Where(n => n is not null).ToList() ?? new List<string?>();

            var configuredBaseName = _ollamaOptions.Model.Split(':')[0];
            var modelAvailable = installedModels.Any(name =>
                string.Equals(name, _ollamaOptions.Model, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name?.Split(':')[0], configuredBaseName, StringComparison.OrdinalIgnoreCase));

            return new AiProviderStatus(
                "Ollama",
                Configured: true,
                ModelAvailable: modelAvailable,
                Model: _ollamaOptions.Model,
                Detail: modelAvailable
                    ? null
                    : $"Model '{_ollamaOptions.Model}' is not installed. Run: ollama pull {_ollamaOptions.Model}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AiProviderStatus(
                "Ollama",
                Configured: true,
                ModelAvailable: false,
                Model: _ollamaOptions.Model,
                Detail: $"Ollama is not running or is unreachable at {_ollamaOptions.BaseUrl}. Start Ollama and try again.");
        }
    }

    private record OllamaTagsResponse([property: JsonPropertyName("models")] List<OllamaTagModel>? Models);

    private record OllamaTagModel([property: JsonPropertyName("name")] string? Name);
}
