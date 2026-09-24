using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// IAiChatClient for the Google Gemini API (models/{model}:generateContent), over plain HttpClient
// like the other providers (ADR-001 §1). JSON output is enforced by the provider
// (responseMimeType: application/json) rather than by prompt wording alone; AiJsonParser's shape
// and semantic validation still run on everything this returns.
//
// Failures are classified into AiProviderException / AiOutputValidationException here, so the
// retry decorator (ResilientAiChatClient) and the API error mapping never need to know about
// Gemini's error format. The API key travels only in the x-goog-api-key header - never in the URL
// (which HttpClient logging records) and never in an exception message.
public partial class GeminiAiChatClient : IAiChatClient
{
    public const string ProviderName = "Gemini";

    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;

    public GeminiAiChatClient(HttpClient httpClient, IOptions<GeminiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
        // Per-attempt timeouts are enforced by ResilientAiChatClient; this is only a backstop.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public string ModelName => _options.Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!_options.HasUsableApiKey)
        {
            throw new AiProviderException(AiFailureKind.NotConfigured, ProviderName,
                "The Gemini API key is not configured. Set Ai:Gemini:ApiKey (user-secrets) or the Ai__Gemini__ApiKey environment variable.");
        }

        var request = new GeminiRequest(
            new GeminiContent(null, [new GeminiPart(systemPrompt)]),
            [new GeminiContent("user", [new GeminiPart(userPrompt)])],
            new GeminiGenerationConfig(
                "application/json",
                _options.Temperature,
                _options.MaxOutputTokens,
                BuildThinkingConfig()));

        using var message = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(_options.Model)}:generateContent")
        {
            Content = JsonContent.Create(request, options: RequestJson)
        };
        message.Headers.Add("x-goog-api-key", _options.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiFailureKind.Unavailable, ProviderName,
                "Could not reach the Gemini API (network error).", providerMessage: Sanitize(ex.Message), inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response, body);
            }

            GeminiResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GeminiResponse>(body, ResponseJson);
            }
            catch (JsonException)
            {
                throw new AiOutputValidationException("The Gemini API returned a response envelope that was not valid JSON.");
            }

            return ExtractText(parsed);
        }
    }

    // Never both fields: Gemini returns 400 when thinkingLevel and thinkingBudget are combined.
    private GeminiThinkingConfig? BuildThinkingConfig()
    {
        if (!string.IsNullOrWhiteSpace(_options.ThinkingLevel))
        {
            return new GeminiThinkingConfig(_options.ThinkingLevel.Trim().ToLowerInvariant(), null);
        }

        return _options.ThinkingBudget is { } budget ? new GeminiThinkingConfig(null, budget) : null;
    }

    private string ExtractText(GeminiResponse? response)
    {
        if (response?.PromptFeedback?.BlockReason is { Length: > 0 } blockReason)
        {
            throw new AiProviderException(AiFailureKind.ContentBlocked, ProviderName,
                $"Gemini blocked the request (reason: {blockReason}). Review the requirement text for content that may trigger safety filters.");
        }

        var candidate = response?.Candidates?.FirstOrDefault();
        if (candidate is null)
        {
            throw new AiOutputValidationException("The Gemini API returned no candidates.");
        }

        switch (candidate.FinishReason)
        {
            case "MAX_TOKENS":
                throw new AiOutputValidationException(
                    $"The AI response was truncated at the configured output limit (Ai:Gemini:MaxOutputTokens={_options.MaxOutputTokens}) before it finished. " +
                    "Increase Ai:Gemini:MaxOutputTokens or reduce the size of the requirement source.",
                    ["Response truncated at the output-token limit."], repairable: false);
            case "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII":
                throw new AiProviderException(AiFailureKind.ContentBlocked, ProviderName,
                    $"Gemini stopped generating (reason: {candidate.FinishReason}).");
        }

        // Skip "thought" parts (thinking summaries) - only the answer text is the JSON payload.
        var text = new StringBuilder();
        foreach (var part in candidate.Content?.Parts ?? [])
        {
            if (part.Thought != true && part.Text is not null) text.Append(part.Text);
        }

        if (text.Length == 0)
        {
            throw new AiOutputValidationException(
                $"The Gemini API returned an empty response (finish reason: {candidate.FinishReason ?? "none"}).");
        }

        return text.ToString();
    }

    private AiProviderException MapError(HttpResponseMessage response, string body)
    {
        GeminiError? error = null;
        try
        {
            error = JsonSerializer.Deserialize<GeminiErrorEnvelope>(body, ResponseJson)?.Error;
        }
        catch (JsonException)
        {
            // Non-JSON error page (proxy, gateway) - fall back to the HTTP status alone.
        }

        var providerMessage = Sanitize(error?.Message ?? $"HTTP {(int)response.StatusCode}");
        var status = error?.Status ?? string.Empty;
        var kind = AiProviderException.KindFromStatus(response.StatusCode);

        // Gemini reports a bad key as 400 INVALID_ARGUMENT ("API key not valid"), not 401.
        if (providerMessage.Contains("API key", StringComparison.OrdinalIgnoreCase)
            && (status is "INVALID_ARGUMENT" or "PERMISSION_DENIED" or "UNAUTHENTICATED"))
        {
            kind = AiFailureKind.Authentication;
        }
        else if (status == "RESOURCE_EXHAUSTED")
        {
            kind = AiFailureKind.RateLimited;
        }
        else if (status == "DEADLINE_EXCEEDED")
        {
            kind = AiFailureKind.Timeout;
        }

        var summary = kind switch
        {
            AiFailureKind.Authentication => "Gemini rejected the configured API key.",
            AiFailureKind.RateLimited => "Gemini rate limit or free-tier quota exceeded.",
            AiFailureKind.Timeout => "Gemini did not finish in time.",
            AiFailureKind.Unavailable => "The Gemini API is temporarily unavailable.",
            _ when response.StatusCode == HttpStatusCode.NotFound =>
                $"Gemini model '{_options.Model}' was not found or is not available to this API key. Check Ai:Gemini:Model.",
            _ => "Gemini rejected the request."
        };

        return new AiProviderException(kind, ProviderName,
            $"{summary} (HTTP {(int)response.StatusCode}{(status.Length > 0 ? " " + status : "")}: {providerMessage})",
            response.StatusCode, providerMessage, ParseRetryDelay(response, error));
    }

    private static TimeSpan? ParseRetryDelay(HttpResponseMessage response, GeminiError? error)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;

        // google.rpc.RetryInfo: {"@type": ".../google.rpc.RetryInfo", "retryDelay": "17s"}
        foreach (var detail in error?.Details ?? [])
        {
            if (detail.TryGetProperty("retryDelay", out var delay) && delay.ValueKind == JsonValueKind.String
                && delay.GetString() is { } value && value.EndsWith('s')
                && double.TryParse(value[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    // Provider messages are surfaced to logs and API clients - make sure no key can ride along.
    private string Sanitize(string message)
    {
        var sanitized = GoogleKeyPattern().Replace(message, "[redacted-key]");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            sanitized = sanitized.Replace(_options.ApiKey.Trim(), "[redacted-key]", StringComparison.Ordinal);
        }

        return sanitized.Length <= 400 ? sanitized : sanitized[..400] + "...";
    }

    [GeneratedRegex("AIza[0-9A-Za-z_\\-]{20,}")]
    private static partial Regex GoogleKeyPattern();

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ResponseJson = new() { PropertyNameCaseInsensitive = true };

    private record GeminiRequest(GeminiContent SystemInstruction, GeminiContent[] Contents, GeminiGenerationConfig GenerationConfig);
    private record GeminiContent(string? Role, GeminiPart[] Parts);
    private record GeminiPart(string Text);
    private record GeminiGenerationConfig(string ResponseMimeType, double? Temperature, int MaxOutputTokens, GeminiThinkingConfig? ThinkingConfig);
    private record GeminiThinkingConfig(string? ThinkingLevel, int? ThinkingBudget);

    private record GeminiResponse(List<GeminiCandidate>? Candidates, GeminiPromptFeedback? PromptFeedback);
    private record GeminiCandidate(GeminiResponseContent? Content, string? FinishReason);
    private record GeminiResponseContent(List<GeminiResponsePart>? Parts);
    private record GeminiResponsePart(string? Text, bool? Thought);
    private record GeminiPromptFeedback(string? BlockReason);

    private record GeminiErrorEnvelope(GeminiError? Error);
    private record GeminiError(int Code, string? Message, string? Status, List<JsonElement>? Details);
}
