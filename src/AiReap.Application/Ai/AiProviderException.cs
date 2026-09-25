using System.Net;

namespace AiReap.Application.Ai;

public enum AiFailureKind
{
    // No usable API key / provider settings — an operator action is needed.
    NotConfigured,
    // The provider refused the credentials (invalid, revoked, or lacking permission).
    Authentication,
    // Rate limit or free-tier quota exhausted (HTTP 429 / RESOURCE_EXHAUSTED).
    RateLimited,
    // No answer within the configured timeout.
    Timeout,
    // Provider outage, 5xx, or network failure.
    Unavailable,
    // The provider rejected the request itself (bad model name, billing, invalid argument).
    RequestRejected,
    // The provider's safety filters blocked the prompt or the response.
    ContentBlocked
}

// Every provider client (Gemini, Groq, Anthropic) translates its transport/HTTP failures into
// this one type, so retry policy and API error mapping are provider-independent. Message and
// ProviderMessage never contain the API key or prompt text.
public class AiProviderException : Exception
{
    public AiProviderException(
        AiFailureKind kind, string provider, string message,
        HttpStatusCode? providerStatusCode = null, string? providerMessage = null,
        TimeSpan? retryAfter = null, Exception? inner = null, bool quotaExhausted = false)
        : base(message, inner)
    {
        Kind = kind;
        Provider = provider;
        ProviderStatusCode = providerStatusCode;
        ProviderMessage = providerMessage;
        RetryAfter = retryAfter;
        QuotaExhausted = quotaExhausted;
    }

    public AiFailureKind Kind { get; }
    public string Provider { get; }
    public HttpStatusCode? ProviderStatusCode { get; }
    public string? ProviderMessage { get; }
    public TimeSpan? RetryAfter { get; }

    // RateLimited only: the provider says a long-window quota is used up (e.g. Gemini's per-day
    // free-tier limit, or a model with no quota at all for this key). Retrying within the same
    // request cannot succeed and would only spend more requests, so the retry decorator fails fast.
    public bool QuotaExhausted { get; }

    // Worth retrying automatically: the same request may well succeed shortly.
    public bool IsTransient => Kind is AiFailureKind.RateLimited or AiFailureKind.Timeout or AiFailureKind.Unavailable;

    public static AiFailureKind KindFromStatus(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => AiFailureKind.Authentication,
        408 => AiFailureKind.Timeout,
        429 => AiFailureKind.RateLimited,
        >= 500 => AiFailureKind.Unavailable,
        _ => AiFailureKind.RequestRejected
    };
}
