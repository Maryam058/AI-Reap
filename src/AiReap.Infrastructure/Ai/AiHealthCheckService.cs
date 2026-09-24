using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Backs the /api/ai/status diagnostic endpoint. Never runs a generation (that would spend quota or
// money on every health check). The Gemini branch calls models.get, which checks both the key and
// that the configured model exists without consuming generation quota. The Anthropic branch only
// reports whether a usable key is configured. The Ollama branch calls Ollama's own local
// /api/tags, which is free, to check both reachability and whether the configured model is
// actually installed.
public class AiHealthCheckService : IAiHealthCheckService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiOptions _aiOptions;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly OllamaOptions _ollamaOptions;
    private readonly GeminiOptions _geminiOptions;

    public AiHealthCheckService(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> aiOptions,
        IOptions<AnthropicOptions> anthropicOptions,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<GeminiOptions> geminiOptions)
    {
        _httpClientFactory = httpClientFactory;
        _aiOptions = aiOptions.Value;
        _anthropicOptions = anthropicOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
        _geminiOptions = geminiOptions.Value;
    }

    public Task<AiProviderStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var provider = string.IsNullOrWhiteSpace(_aiOptions.Provider) ? AiOptions.Gemini : _aiOptions.Provider.Trim();

        if (string.Equals(provider, AiOptions.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return CheckGeminiAsync(cancellationToken);
        }

        return string.Equals(provider, AiOptions.Ollama, StringComparison.OrdinalIgnoreCase)
            ? CheckOllamaAsync(cancellationToken)
            : Task.FromResult(CheckAnthropic());
    }

    private async Task<AiProviderStatus> CheckGeminiAsync(CancellationToken cancellationToken)
    {
        if (!_geminiOptions.HasUsableApiKey)
        {
            return new AiProviderStatus(AiOptions.Gemini, Configured: false, ModelAvailable: false, _geminiOptions.Model,
                "Ai:Gemini:ApiKey is not configured. In Development the canned stub is used instead of Gemini.");
        }

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(_geminiOptions.BaseUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1beta/models/{Uri.EscapeDataString(_geminiOptions.Model)}");
        request.Headers.Add("x-goog-api-key", _geminiOptions.ApiKey.Trim());

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var detail = (int)response.StatusCode switch
            {
                >= 200 and < 300 => null,
                400 or 401 or 403 => "Gemini rejected the configured API key.",
                404 => $"Model '{_geminiOptions.Model}' is not available to this API key. Check Ai:Gemini:Model.",
                429 => "Gemini rate limit or quota currently exceeded.",
                _ => $"Gemini returned HTTP {(int)response.StatusCode}."
            };
            return new AiProviderStatus(AiOptions.Gemini, Configured: true, ModelAvailable: response.IsSuccessStatusCode, _geminiOptions.Model, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AiProviderStatus(AiOptions.Gemini, Configured: true, ModelAvailable: false, _geminiOptions.Model,
                "The Gemini API could not be reached (network error or timeout).");
        }
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
