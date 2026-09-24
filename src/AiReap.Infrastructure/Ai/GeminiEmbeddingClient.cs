using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// §26 — semantic embeddings from the Gemini API (models/{model}:embedContent), using the same API
// key as the chat client, so real RAG needs no second vendor. Replaces the hash stub whenever a
// Gemini key is configured. Key only in the x-goog-api-key header, never logged or echoed.
public partial class GeminiEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;

    public GeminiEmbeddingClient(HttpClient httpClient, IOptions<GeminiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    // Includes the dimensionality: vectors of different sizes must never be compared, and
    // DocumentChunk.EmbeddingModel is what retrieval filters on.
    public string ModelName => $"{_options.EmbeddingModel}@{_options.EmbeddingDimensions}";

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_options.HasUsableApiKey)
        {
            throw new AiProviderException(AiFailureKind.NotConfigured, GeminiAiChatClient.ProviderName,
                "The Gemini API key is not configured, so document embeddings cannot be computed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(_options.EmbeddingModel)}:embedContent")
        {
            Content = JsonContent.Create(new
            {
                model = $"models/{_options.EmbeddingModel}",
                content = new { parts = new[] { new { text } } },
                outputDimensionality = _options.EmbeddingDimensions
            })
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiFailureKind.Unavailable, GeminiAiChatClient.ProviderName,
                "Could not reach the Gemini embeddings API (network error).", inner: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailureKind.Timeout, GeminiAiChatClient.ProviderName,
                "The Gemini embeddings API did not respond in time.", inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string? message = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    message = doc.RootElement.GetProperty("error").GetProperty("message").GetString();
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                }

                var safe = KeyPattern().Replace(message ?? $"HTTP {(int)response.StatusCode}", "[redacted-key]");
                throw new AiProviderException(AiProviderException.KindFromStatus(response.StatusCode), GeminiAiChatClient.ProviderName,
                    $"Gemini embeddings request failed (HTTP {(int)response.StatusCode}): {safe}", response.StatusCode, safe);
            }

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("embedding", out var embedding)
                || !embedding.TryGetProperty("values", out var values)
                || values.GetArrayLength() == 0)
            {
                throw new AiOutputValidationException("The Gemini embeddings API returned no vector.");
            }

            return values.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
    }

    [GeneratedRegex("AIza[0-9A-Za-z_\\-]{20,}")]
    private static partial Regex KeyPattern();
}
