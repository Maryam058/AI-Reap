using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Concrete IAiChatClient implementation for Anthropic's Messages API, reached via plain
// HttpClient rather than a vendor SDK (see ADR-001 §1). All business logic depends only on
// IAiChatClient, so swapping providers means adding one more class like this one.
public class AnthropicAiChatClient : IAiChatClient
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;

    public AnthropicAiChatClient(HttpClient httpClient, IOptions<AnthropicOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri("https://api.anthropic.com/");
        _httpClient.DefaultRequestHeaders.Remove("x-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Remove("anthropic-version");
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ModelName => _options.Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var request = new AnthropicRequest(
            _options.Model,
            _options.MaxTokens,
            systemPrompt,
            new[] { new AnthropicMessage("user", userPrompt) });

        using var httpResponse = await _httpClient.PostAsJsonAsync("v1/messages", request, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var body = await httpResponse.Content.ReadFromJsonAsync<AnthropicResponse>(cancellationToken: cancellationToken);
        var text = body?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;

        return text ?? string.Empty;
    }

    private record AnthropicRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] AnthropicMessage[] Messages);

    private record AnthropicMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record AnthropicResponse(
        [property: JsonPropertyName("content")] List<AnthropicContentBlock>? Content);

    private record AnthropicContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
