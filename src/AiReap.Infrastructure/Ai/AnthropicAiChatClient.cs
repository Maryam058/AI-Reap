using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// IAiChatClient for Anthropic's Messages API (POST /v1/messages), reached via plain HttpClient
// rather than a vendor SDK (see ADR-001 §1). The only chat provider (docs/CLAUDE_SETUP.md).
//
// Failures are classified into AiProviderException / AiOutputValidationException here, so the
// retry decorator (ResilientAiChatClient) and the API error mapping never need to know about
// Anthropic's error format. The key travels only in the x-api-key header of each request - never in
// a URL or an exception message - and provider messages are scrubbed of anything key-shaped.
//
// A response is returned only when it ended normally (stop_reason "end_turn" or "stop_sequence")
// with non-empty text. A response cut off at max_tokens or the context window, or declined by
// Claude's safety classifiers ("refusal"), is never passed on as if it were complete.
public partial class AnthropicAiChatClient : IAiChatClient
{
    public const string ProviderName = "Anthropic";
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;

    public AnthropicAiChatClient(HttpClient httpClient, IOptions<AnthropicOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
        // Per-attempt timeouts are enforced by ResilientAiChatClient (Ai:Anthropic:TimeoutSeconds);
        // HttpClient's own 100s default would otherwise cut long generations short.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    // Recorded in the AI audit trail: the model exactly as configured and sent.
    public string ModelName => _options.Model;

    // Read by AiRouteInspector for the dry-run AI plan.
    public AnthropicOptions Options => _options;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var request = new MessagesRequest(
            _options.Model,
            _options.MaxTokens,
            string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            [new Message("user", userPrompt)],
            string.IsNullOrWhiteSpace(_options.Effort) ? null : new OutputConfig(_options.Effort.Trim().ToLowerInvariant()));

        var (body, requestId) = await PostAsync("v1/messages", request, cancellationToken);

        MessagesResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MessagesResponse>(body, Json);
        }
        catch (JsonException)
        {
            throw new AiOutputValidationException(
                $"The Anthropic API returned a response envelope that was not valid JSON{RequestIdSuffix(requestId)}.");
        }

        return ExtractText(parsed, requestId);
    }

    // POST /v1/messages/count_tokens for exactly what CompleteAsync would send. Free, separately
    // rate-limited, and an estimate (it can include system-added tokens that are not billed). Used
    // only when Ai:Anthropic:UseTokenCountingApi is on (RequestBudgetAiChatClient).
    public async Task<int> CountInputTokensAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var request = new CountTokensRequest(
            _options.Model,
            string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            [new Message("user", userPrompt)]);

        var (body, requestId) = await PostAsync("v1/messages/count_tokens", request, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<CountTokensResponse>(body, Json)?.InputTokens
                ?? throw new AiOutputValidationException($"The Anthropic token count response had no input_tokens{RequestIdSuffix(requestId)}.");
        }
        catch (JsonException)
        {
            throw new AiOutputValidationException($"The Anthropic token count response was not valid JSON{RequestIdSuffix(requestId)}.");
        }
    }

    private void EnsureConfigured()
    {
        if (!_options.HasUsableApiKey)
        {
            throw new AiProviderException(AiFailureKind.NotConfigured, ProviderName,
                "The Anthropic API key is not configured (or is a placeholder). Set Ai:Anthropic:ApiKey (user-secrets) or the " +
                "Ai__Anthropic__ApiKey environment variable. No request was sent.");
        }
    }

    private async Task<(string Body, string? RequestId)> PostAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        message.Headers.Add("x-api-key", _options.ApiKey.Trim());
        message.Headers.Add("anthropic-version", ApiVersion);
        message.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Connection-level failure: whether the request reached Anthropic is unknown.
            throw new AiProviderException(AiFailureKind.Unavailable, ProviderName,
                "Could not reach the Anthropic API (network error).", providerMessage: Sanitize(ex.Message), inner: ex);
        }

        using (response)
        {
            var requestId = RequestId(response);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response, body, requestId);
            }

            return (body, requestId);
        }
    }

    private string ExtractText(MessagesResponse? response, string? requestId)
    {
        if (response is null)
        {
            throw new AiOutputValidationException($"The Anthropic API returned an empty response envelope{RequestIdSuffix(requestId)}.");
        }

        switch (response.StopReason)
        {
            case "max_tokens":
                throw new AiOutputTruncatedException(
                    $"The AI response was truncated at the configured MaxTokens limit (Ai:Anthropic:MaxTokens={_options.MaxTokens}) " +
                    "before it finished, so it was rejected as incomplete. Thinking tokens count toward this limit. " +
                    $"Increase Ai:Anthropic:MaxTokens or reduce the size of the requirement source{RequestIdSuffix(requestId)}.",
                    ["Response truncated at the output-token limit (stop_reason max_tokens)."], "max_tokens");
            case "model_context_window_exceeded":
                throw new AiOutputTruncatedException(
                    "The AI response stopped because the model's context window was exhausted before it finished, so it was " +
                    $"rejected as incomplete. Reduce Ai:Anthropic:MaxRequestTokens or the size of the requirement source{RequestIdSuffix(requestId)}.",
                    ["Response truncated at the context window (stop_reason model_context_window_exceeded)."], "model_context_window_exceeded");
            case "refusal":
                var category = response.StopDetails?.Category is { Length: > 0 } c ? c : "unspecified";
                var explanation = response.StopDetails?.Explanation is { Length: > 0 } e ? $" {Sanitize(e)}" : string.Empty;
                throw new AiProviderException(AiFailureKind.ContentBlocked, ProviderName,
                    $"Claude declined to process this request (refusal category: {category}).{explanation} The response was not used. " +
                    $"Review the requirement text for content that may trigger safety classifiers{RequestIdSuffix(requestId)}.",
                    providerMessage: $"refusal: {category}");
            case "end_turn" or "stop_sequence":
                break;
            default:
                // tool_use / pause_turn cannot occur without tools; anything else is unexpected.
                throw new AiOutputValidationException(
                    $"The Anthropic API returned an unexpected stop reason '{response.StopReason ?? "none"}', so the response was " +
                    $"not treated as complete{RequestIdSuffix(requestId)}.",
                    [$"Unexpected stop_reason '{response.StopReason ?? "none"}'."], repairable: false);
        }

        // Thinking blocks precede the answer; only text blocks are the payload.
        var text = new StringBuilder();
        foreach (var block in response.Content ?? [])
        {
            if (block.Type == "text" && block.Text is not null) text.Append(block.Text);
        }

        if (text.Length == 0)
        {
            throw new AiOutputValidationException($"The Anthropic API returned no text content{RequestIdSuffix(requestId)}.");
        }

        return text.ToString();
    }

    private AiProviderException MapError(HttpResponseMessage response, string body, string? requestId)
    {
        ErrorDetail? error = null;
        try
        {
            error = JsonSerializer.Deserialize<ErrorEnvelope>(body, Json)?.Error;
        }
        catch (JsonException)
        {
            // Non-JSON error page (proxy, gateway, Cloudflare 413) - fall back to the HTTP status alone.
        }

        var status = (int)response.StatusCode;
        var errorType = error?.Type ?? string.Empty;
        var providerMessage = Sanitize(error?.Message ?? $"HTTP {status}");
        var lowered = providerMessage.ToLowerInvariant();
        var billing = errorType == "billing_error" || status == 402
            || lowered.Contains("credit balance") || lowered.Contains("spend limit") || lowered.Contains("spend cap");
        var promptTooLong = status == 400 && (lowered.Contains("prompt is too long") || lowered.Contains("context window"));

        var kind = status switch
        {
            401 or 403 => AiFailureKind.Authentication,
            408 or 504 => AiFailureKind.Timeout,
            429 => AiFailureKind.RateLimited,
            >= 500 => AiFailureKind.Unavailable,
            _ => AiFailureKind.RequestRejected
        };
        var quotaExhausted = kind == AiFailureKind.RateLimited && billing && response.Headers.RetryAfter is null;

        var summary = kind switch
        {
            AiFailureKind.Authentication when status == 403 =>
                "Anthropic refused the request: the configured API key lacks permission for this model or workspace (HTTP 403). " +
                "Check the key's workspace access in the Claude Console.",
            AiFailureKind.Authentication =>
                "Anthropic rejected the configured API key (HTTP 401). Check Ai:Anthropic:ApiKey; the key may be invalid, revoked or expired.",
            AiFailureKind.RateLimited when quotaExhausted =>
                "Anthropic usage or spend cap reached (HTTP 429 without a retry delay); requests will keep failing until access resumes.",
            AiFailureKind.RateLimited => "Anthropic rate limit reached (HTTP 429).",
            AiFailureKind.Timeout =>
                $"Anthropic did not finish processing in time (HTTP {status}). The request may already have been processed and billed.",
            AiFailureKind.Unavailable when status == 529 => "The Anthropic API is temporarily overloaded (HTTP 529).",
            AiFailureKind.Unavailable => $"The Anthropic API is temporarily unavailable (HTTP {status}).",
            _ when billing => $"Anthropic rejected the request because of a billing, credit or spend-limit problem (HTTP {status}). " +
                "Check the organization's billing and spend limits in the Claude Console.",
            _ when promptTooLong => "Anthropic rejected the request as larger than the model's context window (HTTP 400). " +
                "Reduce Ai:Anthropic:MaxRequestTokens or the size of the requirement source.",
            _ when status == 413 => "Anthropic rejected the request as larger than the API's maximum request size (HTTP 413).",
            _ when status == 404 => $"Anthropic model '{_options.Model}' was not found or is not available to this API key (HTTP 404). " +
                "Check Ai:Anthropic:Model.",
            _ => $"Anthropic rejected the request (HTTP {status}{(errorType.Length > 0 ? " " + errorType : "")})."
        };

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;

        // ProviderMessage is what the API shows a client for a rejected request, so it carries the
        // actionable summary too; both are built only from sanitized provider text and our own wording.
        var full = $"{summary} Provider detail: {providerMessage}{RequestIdSuffix(requestId)}";
        return new AiProviderException(kind, ProviderName, full, response.StatusCode, full, retryAfter, quotaExhausted: quotaExhausted);
    }

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("request-id", out var values) ? values.FirstOrDefault() : null;

    private static string RequestIdSuffix(string? requestId) =>
        string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" (Anthropic request-id {requestId})";

    // Provider messages are surfaced to logs and API clients - make sure no key can ride along.
    private string Sanitize(string message)
    {
        var sanitized = AnthropicKeyPattern().Replace(message, "[redacted-key]");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            sanitized = sanitized.Replace(_options.ApiKey.Trim(), "[redacted-key]", StringComparison.Ordinal);
        }

        return sanitized.Length <= 400 ? sanitized : sanitized[..400] + "...";
    }

    [GeneratedRegex("sk-ant-[0-9A-Za-z_\\-]{8,}")]
    private static partial Regex AnthropicKeyPattern();

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private record MessagesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string? System,
        [property: JsonPropertyName("messages")] Message[] Messages,
        [property: JsonPropertyName("output_config")] OutputConfig? OutputConfig);

    private record CountTokensRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("system")] string? System,
        [property: JsonPropertyName("messages")] Message[] Messages);

    private record OutputConfig([property: JsonPropertyName("effort")] string Effort);

    private record Message(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private record MessagesResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("content")] List<ContentBlock>? Content,
        [property: JsonPropertyName("stop_reason")] string? StopReason,
        [property: JsonPropertyName("stop_details")] StopDetails? StopDetails);

    private record ContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    private record StopDetails(
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("explanation")] string? Explanation);

    private record CountTokensResponse([property: JsonPropertyName("input_tokens")] int? InputTokens);

    private record ErrorEnvelope([property: JsonPropertyName("error")] ErrorDetail? Error);

    private record ErrorDetail(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("message")] string? Message);
}
