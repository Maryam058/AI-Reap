using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// IAiChatClient for Groq's OpenAI-compatible chat completions endpoint, over plain HttpClient like
// the other providers (ADR-001 §1). The fallback behind Gemini (FallbackAiChatClient). JSON output
// is enforced by the provider (response_format: json_object) and the message content is returned
// as-is - the same raw-JSON-text contract GeminiAiChatClient returns - so AiJsonParser and
// GenerationSupport.CallAiAndParseAsync work unchanged.
//
// Failures are classified into AiProviderException / AiOutputValidationException here, like
// Gemini. The key travels only in the per-request Authorization header - never in the URL, a
// default header, or an exception message.
//
// Last line of the request budget (Ai:Groq:MaxRequestTokens): RequestBudgetAiChatClient condenses
// prompts before they get here, but this client checks the exact messages it is about to send
// regardless of how it was wired, and caps max_completion_tokens so that estimated input plus the
// most output the model may produce stays within the budget. A request that still doesn't fit is
// refused here, before any HTTP call.
public partial class GroqAiChatClient : IAiChatClient
{
    public const string ProviderName = "Groq";

    private readonly HttpClient _httpClient;
    private readonly GroqOptions _options;
    private readonly ILogger _logger;

    public GroqAiChatClient(HttpClient httpClient, IOptions<GroqOptions> options, ILogger<GroqAiChatClient>? logger = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger ?? (ILogger)NullLogger.Instance;

        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
        // Per-attempt timeouts are enforced by ResilientAiChatClient; this is only a backstop.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    // Prefixed so the AI audit trail (AIExecution.Model) shows the response came from Groq, not
    // from the model vendor's own API.
    public string ModelName => $"groq:{_options.Model}";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!_options.HasUsableApiKey)
        {
            throw new AiProviderException(AiFailureKind.NotConfigured, ProviderName,
                $"The Groq API key is not configured. Set Ai:Groq:ApiKey (user-secrets), the Ai__Groq__ApiKey or {GroqOptions.ConventionalKeyVariable} environment variable.");
        }

        var maxCompletionTokens = BudgetedMaxCompletionTokens(systemPrompt, userPrompt);

        var request = new GroqRequest(
            _options.Model,
            [new GroqMessage("system", systemPrompt), new GroqMessage("user", userPrompt)],
            new GroqResponseFormat("json_object"),
            _options.Temperature,
            maxCompletionTokens,
            string.IsNullOrWhiteSpace(_options.ReasoningEffort) ? null : _options.ReasoningEffort.Trim().ToLowerInvariant());

        using var message = new HttpRequestMessage(HttpMethod.Post, "openai/v1/chat/completions")
        {
            Content = JsonContent.Create(request, options: RequestJson)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiFailureKind.Unavailable, ProviderName,
                "Could not reach the Groq API (network error).", providerMessage: Sanitize(ex.Message), inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response, body);
            }

            GroqResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GroqResponse>(body, ResponseJson);
            }
            catch (JsonException)
            {
                throw new AiOutputValidationException("The Groq API returned a response envelope that was not valid JSON.");
            }

            return ExtractText(parsed, maxCompletionTokens);
        }
    }

    // max_completion_tokens for this request: the configured value, lowered so that the estimated
    // input plus the output allowance never exceeds Ai:Groq:MaxRequestTokens. Throws (no HTTP call)
    // when the input leaves less than Ai:Groq:ReservedOutputTokens for the response.
    private int BudgetedMaxCompletionTokens(string systemPrompt, string userPrompt)
    {
        var configured = _options.MaxCompletionTokens;
        if (_options.MaxRequestTokens <= 0)
        {
            return configured;
        }

        var inputTokens = PromptBudgetReducer.EstimateRequestTokens(systemPrompt, userPrompt);
        var reserved = Math.Max(0, _options.ReservedOutputTokens);
        if (inputTokens + reserved > _options.MaxRequestTokens)
        {
            _logger.LogWarning(
                "Groq request not sent: ~{InputTokens} estimated input tokens + {Reserved} reserved output tokens exceed MaxRequestTokens {MaxRequestTokens}.",
                inputTokens, reserved, _options.MaxRequestTokens);
            throw RequestBudgetAiChatClient.TooLarge(ProviderName, BudgetSetting, _options.MaxRequestTokens, inputTokens, inner: null);
        }

        var maxCompletionTokens = Math.Min(configured, _options.MaxRequestTokens - inputTokens);
        _logger.LogInformation(
            "Groq request: ~{InputTokens} estimated input tokens, max_completion_tokens {MaxCompletionTokens} " +
            "(configured {Configured}), MaxRequestTokens {MaxRequestTokens}, ReservedOutputTokens {Reserved}.",
            inputTokens, maxCompletionTokens, configured, _options.MaxRequestTokens, reserved);
        return maxCompletionTokens;
    }

    private const string BudgetSetting = $"{GroqOptions.SectionName}:{nameof(GroqOptions.MaxRequestTokens)}";

    private string ExtractText(GroqResponse? response, int maxCompletionTokens)
    {
        var choice = response?.Choices?.FirstOrDefault();
        if (choice is null)
        {
            throw new AiOutputValidationException("The Groq API returned no choices.");
        }

        switch (choice.FinishReason)
        {
            case "length":
                throw new AiOutputValidationException(
                    maxCompletionTokens < _options.MaxCompletionTokens
                        ? $"The AI response was truncated at {maxCompletionTokens} output tokens before it finished: the rest of the " +
                          $"{BudgetSetting}={_options.MaxRequestTokens} request budget after the input (Ai:Groq:MaxCompletionTokens={_options.MaxCompletionTokens}). " +
                          "Reduce the size of the requirement source, or retry when the primary AI provider is available again."
                        : $"The AI response was truncated at the configured output limit (Ai:Groq:MaxCompletionTokens={_options.MaxCompletionTokens}) before it finished. " +
                          "Increase Ai:Groq:MaxCompletionTokens or reduce the size of the requirement source.",
                    ["Response truncated at the output-token limit."], repairable: false);
            case "content_filter":
                throw new AiProviderException(AiFailureKind.ContentBlocked, ProviderName,
                    "Groq stopped generating (reason: content_filter).");
        }

        // Only the answer content is the JSON payload; gpt-oss reasoning arrives in a separate field.
        var text = choice.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AiOutputValidationException(
                $"The Groq API returned an empty response (finish reason: {choice.FinishReason ?? "none"}).");
        }

        return text;
    }

    private Exception MapError(HttpResponseMessage response, string body)
    {
        GroqError? error = null;
        try
        {
            error = JsonSerializer.Deserialize<GroqErrorEnvelope>(body, ResponseJson)?.Error;
        }
        catch (JsonException)
        {
            // Non-JSON error page (proxy, gateway) - fall back to the HTTP status alone.
        }

        var status = (int)response.StatusCode;
        var code = error?.Code ?? string.Empty;
        var providerMessage = Sanitize(error?.Message ?? $"HTTP {status}");

        // The model could not produce JSON matching json_object mode. This is an output problem,
        // not a provider fault: GenerationSupport's one repair attempt handles it like any other
        // invalid response. failed_generation is not surfaced - it may quote requirement text.
        if (code == "json_validate_failed")
        {
            throw new AiOutputValidationException(
                "Groq could not produce a valid JSON response for this request.",
                ["The response was not a single valid JSON object."]);
        }

        var kind = status switch
        {
            // Groq's flex tier answers 498 when it has no capacity right now.
            498 => AiFailureKind.Unavailable,
            _ => AiProviderException.KindFromStatus(response.StatusCode)
        };

        // A 429 on a per-day limit (RPD/TPD) won't clear within this request.
        var quotaExhausted = kind == AiFailureKind.RateLimited
            && providerMessage.Contains("per day", StringComparison.OrdinalIgnoreCase);
        var rateLimitType = kind == AiFailureKind.RateLimited ? RateLimitType(providerMessage) : null;

        var summary = kind switch
        {
            AiFailureKind.Authentication => "Groq rejected the configured API key.",
            AiFailureKind.RateLimited when quotaExhausted => "Groq daily quota exhausted.",
            AiFailureKind.RateLimited => "Groq rate limit reached.",
            AiFailureKind.Timeout => "Groq did not finish in time.",
            AiFailureKind.Unavailable => "The Groq API is temporarily unavailable.",
            _ when response.StatusCode == HttpStatusCode.NotFound || code == "model_not_found" =>
                $"Groq model '{_options.Model}' was not found or is not available to this API key. Check Ai:Groq:Model.",
            _ when response.StatusCode == HttpStatusCode.RequestEntityTooLarge =>
                "The request is larger than Groq allows for this model/tier (tokens per minute). Reduce the size of the requirement source.",
            _ => "Groq rejected the request."
        };

        return new AiProviderException(kind, ProviderName,
            $"{summary} (HTTP {status}{(code.Length > 0 ? " " + code : "")}: {providerMessage})",
            response.StatusCode, providerMessage, RetryDelay(response.Headers), quotaExhausted: quotaExhausted,
            rateLimitType: rateLimitType);
    }

    // The limit Groq names, e.g. "... on tokens per day (TPD): Limit 200000 ...". Only when the
    // message names exactly one limit; anything else (none, or several) is not guessed.
    internal static string? RateLimitType(string providerMessage)
    {
        var named = RateLimitPattern().Matches(providerMessage)
            .Select(m => m.Groups["code"].Success
                ? m.Groups["code"].Value.ToUpperInvariant()
                : $"{char.ToUpperInvariant(m.Groups["unit"].Value[0])}P{char.ToUpperInvariant(m.Groups["window"].Value[0])}")
            .Distinct()
            .ToList();
        return named.Count == 1 ? named[0] : null;
    }

    [GeneratedRegex(@"\((?<code>TPD|RPD|TPM|RPM)\)|\b(?<unit>tokens|requests) per (?<window>day|minute)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();

    private static TimeSpan? RetryDelay(HttpResponseHeaders headers)
    {
        var delay = headers.RetryAfter?.Delta ?? (headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        return delay > TimeSpan.Zero ? delay : null;
    }

    // Provider messages are surfaced to logs and API clients - make sure no key can ride along.
    private string Sanitize(string message)
    {
        var sanitized = GroqKeyPattern().Replace(message, "[redacted-key]");
        // Rate-limit messages name the account's organization ("in organization `org_...`").
        sanitized = GroqOrganizationPattern().Replace(sanitized, "org_[redacted]");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            sanitized = sanitized.Replace(_options.ApiKey.Trim(), "[redacted-key]", StringComparison.Ordinal);
        }

        return sanitized.Length <= 400 ? sanitized : sanitized[..400] + "...";
    }

    [GeneratedRegex("gsk_[0-9A-Za-z]{20,}")]
    private static partial Regex GroqKeyPattern();

    [GeneratedRegex("org_[0-9A-Za-z]+")]
    private static partial Regex GroqOrganizationPattern();

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private record GroqRequest(
        string Model, GroqMessage[] Messages, GroqResponseFormat ResponseFormat,
        double? Temperature, int MaxCompletionTokens, string? ReasoningEffort);
    private record GroqMessage(string Role, string Content);
    private record GroqResponseFormat(string Type);

    private record GroqResponse(List<GroqChoice>? Choices);
    private record GroqChoice(GroqResponseMessage? Message, string? FinishReason);
    private record GroqResponseMessage(string? Content);

    private record GroqErrorEnvelope(GroqError? Error);
    private record GroqError(string? Message, string? Type, string? Code);
}
