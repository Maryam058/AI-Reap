using System.Net.Http.Headers;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Backs the /api/ai/status diagnostic endpoint. Never runs a generation (that would spend quota or
// money on every health check). The Gemini and Groq checks call each provider's free model-lookup
// endpoint, which verifies both the key and that the configured model exists without consuming
// generation quota; the configured fallback provider is checked alongside the primary. The
// Anthropic branch only reports whether a usable key is configured.
public class AiHealthCheckService : IAiHealthCheckService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiOptions _aiOptions;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly GeminiOptions _geminiOptions;
    private readonly GroqOptions _groqOptions;

    public AiHealthCheckService(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> aiOptions,
        IOptions<AnthropicOptions> anthropicOptions,
        IOptions<GeminiOptions> geminiOptions,
        IOptions<GroqOptions> groqOptions)
    {
        _httpClientFactory = httpClientFactory;
        _aiOptions = aiOptions.Value;
        _anthropicOptions = anthropicOptions.Value;
        _geminiOptions = geminiOptions.Value;
        _groqOptions = groqOptions.Value;
    }

    public async Task<AiProviderStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var provider = string.IsNullOrWhiteSpace(_aiOptions.Provider) ? AiOptions.Gemini : _aiOptions.Provider.Trim();

        if (string.Equals(provider, AiOptions.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            return CheckAnthropic();
        }

        var primary = await CheckCloudAsync(provider, cancellationToken);
        var fallbackName = _aiOptions.FallbackProvider?.Trim();
        if (string.IsNullOrEmpty(fallbackName)
            || string.Equals(fallbackName, "None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fallbackName, provider, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        return primary with { Fallback = await CheckCloudAsync(fallbackName, cancellationToken) };
    }

    private Task<AiProviderStatus> CheckCloudAsync(string provider, CancellationToken cancellationToken)
    {
        if (string.Equals(provider, AiOptions.Groq, StringComparison.OrdinalIgnoreCase))
        {
            // Model ids contain '/' ("openai/gpt-oss-120b"), which Groq expects unescaped in the path.
            var path = "openai/v1/models/" + string.Join('/', _groqOptions.Model.Split('/').Select(Uri.EscapeDataString));
            return CheckAsync(AiOptions.Groq, _groqOptions.HasUsableApiKey, "Ai:Groq:ApiKey", _groqOptions.BaseUrl, path,
                _groqOptions.Model, "Ai:Groq:Model",
                request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _groqOptions.ApiKey.Trim()),
                cancellationToken);
        }

        return CheckAsync(AiOptions.Gemini, _geminiOptions.HasUsableApiKey, "Ai:Gemini:ApiKey", _geminiOptions.BaseUrl,
            $"v1beta/models/{Uri.EscapeDataString(_geminiOptions.Model)}", _geminiOptions.Model, "Ai:Gemini:Model",
            request => request.Headers.Add("x-goog-api-key", _geminiOptions.ApiKey.Trim()),
            cancellationToken);
    }

    private async Task<AiProviderStatus> CheckAsync(
        string provider, bool hasKey, string keySetting, string baseUrl, string modelPath, string model, string modelSetting,
        Action<HttpRequestMessage> authenticate, CancellationToken cancellationToken)
    {
        if (!hasKey)
        {
            return new AiProviderStatus(provider, Configured: false, ModelAvailable: false, model,
                $"{keySetting} is not configured. With no Gemini or Groq key, Development uses the canned stub instead of real AI.");
        }

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(baseUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        using var request = new HttpRequestMessage(HttpMethod.Get, modelPath);
        authenticate(request);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var detail = (int)response.StatusCode switch
            {
                >= 200 and < 300 => null,
                400 or 401 or 403 => $"{provider} rejected the configured API key.",
                404 => $"Model '{model}' is not available to this API key. Check {modelSetting}.",
                429 => $"{provider} rate limit or quota currently exceeded.",
                _ => $"{provider} returned HTTP {(int)response.StatusCode}."
            };
            return new AiProviderStatus(provider, Configured: true, ModelAvailable: response.IsSuccessStatusCode, model, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AiProviderStatus(provider, Configured: true, ModelAvailable: false, model,
                $"The {provider} API could not be reached (network error or timeout).");
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
}
