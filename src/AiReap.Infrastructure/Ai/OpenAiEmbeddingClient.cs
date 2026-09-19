using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// IEmbeddingClient over OpenAI's Embeddings API, reached via plain HttpClient like
// AnthropicAiChatClient. Chosen per ADR-001 §2: Anthropic (the chat provider) has no
// embeddings endpoint, so RAG needs a second vendor - OpenAI text-embedding-3-small was
// selected as cheap and well-supported.
public class OpenAiEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;

    public OpenAiEmbeddingClient(HttpClient httpClient, IOptions<OpenAiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri("https://api.openai.com/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ModelName => _options.Model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new EmbeddingRequest(_options.Model, text);

        using var httpResponse = await _httpClient.PostAsJsonAsync("v1/embeddings", request, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var body = await httpResponse.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken);
        return body?.Data?.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
    }

    private record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingData>? Data);

    private record EmbeddingData(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
