using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Concrete IAiChatClient implementation for a locally-running Ollama instance, reached via
// plain HttpClient (see ADR-001 §1, same approach as AnthropicAiChatClient). Exists so
// AI-REAP can run its full AI pipeline with no Anthropic API charges during development/demo -
// see docs/OLLAMA_SETUP.md for setup. All business logic depends only on IAiChatClient, so
// this adds one more implementation with no changes to any caller.
public class OllamaAiChatClient : IAiChatClient
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;

    public OllamaAiChatClient(HttpClient httpClient, IOptions<OllamaOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public string ModelName => _options.Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var request = new OllamaChatRequest(
            _options.Model,
            new[]
            {
                new OllamaMessage("system", systemPrompt),
                new OllamaMessage("user", userPrompt)
            },
            Stream: false,
            // Every AI-REAP caller feeds the response straight into AiJsonParser (REAP-005).
            // Ollama's "json" format constrains generation to valid JSON at the decoding level,
            // rather than relying on the system prompt's "respond with ONLY JSON" instruction
            // alone - AiJsonParser's markdown-fence stripping stays in place as a defensive
            // fallback, but this avoids needing it in the normal case.
            Format: "json",
            Options: new OllamaRequestOptions(_options.NumCtx));

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // A refused/unreachable connection (Ollama not started, wrong port, firewalled) fails
            // here before any HTTP status exists - EnsureSuccessStatusCode-style handling below
            // never runs, so this is the only place that failure can be turned into an actionable
            // message instead of a raw socket exception.
            throw new AiOutputValidationException(
                $"Ollama is not running or is unreachable at {_httpClient.BaseAddress}. Start Ollama and make sure " +
                $"the configured model ('{_options.Model}') is installed (run: ollama pull {_options.Model}).",
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Distinguished from caller-requested cancellation by checking the caller's own
            // token: HttpClient's internal timeout also throws TaskCanceledException, but the
            // caller's cancellationToken is not the one that fired.
            throw new AiOutputValidationException(
                $"Ollama did not respond within {_options.TimeoutSeconds}s at {_httpClient.BaseAddress}. The model " +
                "may still be loading (the first call after Ollama starts can be slow) or the machine may be " +
                $"under-resourced for '{_options.Model}'.",
                ex);
        }

        using var response = httpResponse;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var looksLikeMissingModel = response.StatusCode == HttpStatusCode.NotFound
                || errorBody.Contains("not found", StringComparison.OrdinalIgnoreCase);

            var message = looksLikeMissingModel
                ? $"Ollama model '{_options.Model}' is not installed. Run: ollama pull {_options.Model}"
                : $"Ollama request failed with status {(int)response.StatusCode} ({response.StatusCode}): {errorBody}";

            throw new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
        }

        OllamaChatResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new AiOutputValidationException("Ollama returned a response that was not valid JSON.", ex);
        }

        if (!string.IsNullOrEmpty(body?.Error))
        {
            throw new AiOutputValidationException($"Ollama reported an error: {body.Error}");
        }

        // Mirrors AnthropicAiChatClient's stop_reason "max_tokens" handling: a response cut off
        // by the context window is not valid JSON and would otherwise surface as an opaque parse
        // failure further up the pipeline - fail here with the actionable reason instead.
        if (body?.DoneReason == "length")
        {
            throw new AiOutputValidationException(
                "The AI response was truncated because it reached the model's context/output limit " +
                $"(Ai:Ollama:NumCtx={_options.NumCtx}). Increase Ai:Ollama:NumCtx or reduce the size of the requirement source.");
        }

        var text = body?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AiOutputValidationException("Ollama returned an empty response.");
        }

        return text;
    }

    private record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OllamaMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("options")] OllamaRequestOptions Options);

    private record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record OllamaRequestOptions(
        [property: JsonPropertyName("num_ctx")] int NumCtx);

    private record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaResponseMessage? Message,
        [property: JsonPropertyName("done_reason")] string? DoneReason,
        [property: JsonPropertyName("error")] string? Error);

    private record OllamaResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}
