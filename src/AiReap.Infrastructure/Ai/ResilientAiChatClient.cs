using AiReap.Application.Ai;
using Microsoft.Extensions.Logging;

namespace AiReap.Infrastructure.Ai;

public class AiResilienceOptions
{
    public const string SectionName = "Ai:Resilience";

    // Per-attempt limit; a request source of a few pages usually completes well inside this.
    public int TimeoutSeconds { get; set; } = 120;

    // Total attempts including the first. Only transient failures (429, 5xx, network, timeout) retry;
    // 429 is further capped by MaxRateLimitAttempts.
    public int MaxAttempts { get; set; } = 3;

    public int BaseDelayMilliseconds { get; set; } = 1000;

    public int MaxDelaySeconds { get; set; } = 20;

    // A provider asking us to wait longer than this (e.g. a daily free-tier quota) is not worth
    // holding the HTTP request open for - fail immediately with a rate-limit error instead.
    public int MaxRetryAfterSeconds { get; set; } = 30;

    // Total attempts (including the first) when the provider answers 429. Lower than MaxAttempts:
    // every retry of a rate-limited request spends more of the same quota, so one retry (after the
    // provider's own Retry-After when given) is enough to ride out a short per-minute limit.
    public int MaxRateLimitAttempts { get; set; } = 2;

    // Total attempts (including the first) when an attempt times out; 0 means MaxAttempts. A request
    // that ran out its whole per-attempt timeout was usually still processed (and charged) by the
    // provider, so resending it at once spends that quota again for the same likely outcome.
    public int MaxTimeoutAttempts { get; set; }

    // For a primary provider that has a fallback (FallbackAiChatClient): a 429 goes straight to the
    // fallback instead of waiting to retry and spending more of the primary's quota.
    public AiResilienceOptions WithoutRateLimitRetry() => new()
    {
        TimeoutSeconds = TimeoutSeconds,
        MaxAttempts = MaxAttempts,
        BaseDelayMilliseconds = BaseDelayMilliseconds,
        MaxDelaySeconds = MaxDelaySeconds,
        MaxRetryAfterSeconds = MaxRetryAfterSeconds,
        MaxRateLimitAttempts = 1,
        MaxTimeoutAttempts = MaxTimeoutAttempts
    };

    // For a provider whose per-minute token window refills gradually (Groq's free tier): its
    // Retry-After hints are often sub-second and too optimistic, so more patient 429 retries are
    // allowed (each waiting at least the exponential backoff - see ResilientAiChatClient).
    public AiResilienceOptions WithRateLimitAttempts(int attempts) => new()
    {
        TimeoutSeconds = TimeoutSeconds,
        MaxAttempts = Math.Max(MaxAttempts, attempts),
        BaseDelayMilliseconds = BaseDelayMilliseconds,
        MaxDelaySeconds = MaxDelaySeconds,
        MaxRetryAfterSeconds = MaxRetryAfterSeconds,
        MaxRateLimitAttempts = Math.Max(MaxRateLimitAttempts, attempts),
        MaxTimeoutAttempts = MaxTimeoutAttempts
    };

    // For a provider whose requests can legitimately run long (Gemini on a large requirement source):
    // its own per-attempt timeout and timeout-retry limit; everything else is kept.
    public AiResilienceOptions WithTimeout(int timeoutSeconds, int maxTimeoutAttempts) => new()
    {
        TimeoutSeconds = timeoutSeconds,
        MaxAttempts = MaxAttempts,
        BaseDelayMilliseconds = BaseDelayMilliseconds,
        MaxDelaySeconds = MaxDelaySeconds,
        MaxRetryAfterSeconds = MaxRetryAfterSeconds,
        MaxRateLimitAttempts = MaxRateLimitAttempts,
        MaxTimeoutAttempts = maxTimeoutAttempts
    };
}

// Wraps every real provider client (Gemini, Groq, Anthropic) with the same timeout, retry and
// error-classification policy, so providers stay thin and the policy is tested once. The stub is
// never wrapped. Validation failures (AiOutputValidationException) pass straight through: retrying
// the identical request won't fix them - GenerationSupport does a separate repair attempt instead.
public sealed class ResilientAiChatClient : IAiChatClient
{
    private readonly string _providerName;
    private readonly AiResilienceOptions _options;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ResilientAiChatClient(
        IAiChatClient inner, string providerName, AiResilienceOptions options, ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Inner = inner;
        _providerName = providerName;
        _options = options;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public IAiChatClient Inner { get; }

    // The policy in effect (DependencyInjection derives a per-provider copy).
    public AiResilienceOptions Options => _options;

    public string ProviderName => _providerName;

    public string ModelName => Inner.ModelName;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        for (var attempt = 1; ; attempt++)
        {
            AiProviderException failure;
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attemptCts.CancelAfter(timeout);
                try
                {
                    return await Inner.CompleteAsync(systemPrompt, userPrompt, attemptCts.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Our timeout fired, not the caller's cancellation.
                    failure = new AiProviderException(AiFailureKind.Timeout, _providerName,
                        $"{_providerName} did not respond within {timeout.TotalSeconds:0}s.", inner: ex);
                }
                catch (AiProviderException ex)
                {
                    failure = ex;
                }
                catch (HttpRequestException ex)
                {
                    failure = Classify(ex);
                }
            }

            if (!failure.IsTransient)
            {
                throw failure;
            }

            if (failure is { Kind: AiFailureKind.RateLimited, QuotaExhausted: true })
            {
                _logger.LogWarning(
                    "AI provider {Provider} reports its quota is exhausted (daily or zero-limit); not retrying.", _providerName);
                throw failure;
            }

            if (failure.RetryAfter is { } retryAfter && retryAfter > TimeSpan.FromSeconds(_options.MaxRetryAfterSeconds))
            {
                _logger.LogWarning(
                    "AI provider {Provider} asked to retry after {RetryAfterSeconds:0}s, beyond the {Max}s limit; not retrying.",
                    _providerName, retryAfter.TotalSeconds, _options.MaxRetryAfterSeconds);
                throw failure;
            }

            var attemptLimit = failure.Kind switch
            {
                AiFailureKind.RateLimited => Math.Clamp(_options.MaxRateLimitAttempts, 1, maxAttempts),
                AiFailureKind.Timeout when _options.MaxTimeoutAttempts > 0 => Math.Clamp(_options.MaxTimeoutAttempts, 1, maxAttempts),
                _ => maxAttempts
            };
            if (attempt >= attemptLimit)
            {
                _logger.LogWarning("AI provider {Provider} still failing after {Attempts} attempt(s): {Kind}", _providerName, attempt, failure.Kind);
                throw failure;
            }

            // A provider's hint is a minimum, not a promise: a per-minute token window may still be
            // full after a sub-second hint, so a 429 waits at least the growing backoff as well.
            var backoff = BackoffDelay(attempt);
            var wait = failure.RetryAfter is { } hint
                ? (failure.Kind == AiFailureKind.RateLimited && hint < backoff ? backoff : hint)
                : backoff;
            _logger.LogWarning(
                "AI provider {Provider} transient failure ({Kind}, status {Status}) on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.",
                _providerName, failure.Kind, (int?)failure.ProviderStatusCode, attempt, attemptLimit, (int)wait.TotalMilliseconds);
            await _delay(wait, cancellationToken);
        }
    }

    private TimeSpan BackoffDelay(int attempt)
    {
        var exponential = _options.BaseDelayMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * _options.BaseDelayMilliseconds;
        var capped = Math.Min(exponential + jitter, _options.MaxDelaySeconds * 1000.0);
        return TimeSpan.FromMilliseconds(Math.Max(0, capped));
    }

    // Providers that predate AiProviderException (Anthropic) throw HttpRequestException;
    // classify them here so they get the same retry policy and API error mapping as Gemini. The
    // message is kept (it holds the provider's error text, never the key), trimmed for size.
    private AiProviderException Classify(HttpRequestException ex)
    {
        var kind = ex.StatusCode is { } status ? AiProviderException.KindFromStatus(status) : AiFailureKind.Unavailable;
        var message = ex.Message.Length <= 400 ? ex.Message : ex.Message[..400] + "...";
        return new AiProviderException(kind, _providerName, message, ex.StatusCode, message, inner: ex);
    }
}
