using AiReap.Application.Ai;
using Microsoft.Extensions.Logging;

namespace AiReap.Infrastructure.Ai;

public class AiResilienceOptions
{
    public const string SectionName = "Ai:Resilience";

    // Per-attempt limit; a request source of a few pages usually completes well inside this.
    public int TimeoutSeconds { get; set; } = 120;

    // Total attempts including the first. Only transient failures (429, 5xx, network, timeout) retry.
    public int MaxAttempts { get; set; } = 3;

    public int BaseDelayMilliseconds { get; set; } = 1000;

    public int MaxDelaySeconds { get; set; } = 20;

    // A provider asking us to wait longer than this (e.g. a daily free-tier quota) is not worth
    // holding the HTTP request open for - fail immediately with a rate-limit error instead.
    public int MaxRetryAfterSeconds { get; set; } = 30;
}

// Wraps every real provider client (Gemini, Anthropic, Ollama) with the same timeout, retry and
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

            if (failure.RetryAfter is { } retryAfter && retryAfter > TimeSpan.FromSeconds(_options.MaxRetryAfterSeconds))
            {
                _logger.LogWarning(
                    "AI provider {Provider} asked to retry after {RetryAfterSeconds:0}s, beyond the {Max}s limit; not retrying.",
                    _providerName, retryAfter.TotalSeconds, _options.MaxRetryAfterSeconds);
                throw failure;
            }

            if (attempt >= maxAttempts)
            {
                _logger.LogWarning("AI provider {Provider} still failing after {Attempts} attempt(s): {Kind}", _providerName, attempt, failure.Kind);
                throw failure;
            }

            var wait = failure.RetryAfter ?? BackoffDelay(attempt);
            _logger.LogWarning(
                "AI provider {Provider} transient failure ({Kind}, status {Status}) on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.",
                _providerName, failure.Kind, (int?)failure.ProviderStatusCode, attempt, maxAttempts, (int)wait.TotalMilliseconds);
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

    // Providers that predate AiProviderException (Anthropic, Ollama) throw HttpRequestException;
    // classify them here so they get the same retry policy and API error mapping as Gemini. The
    // message is kept (it holds the provider's error text, never the key), trimmed for size.
    private AiProviderException Classify(HttpRequestException ex)
    {
        var kind = ex.StatusCode is { } status ? AiProviderException.KindFromStatus(status) : AiFailureKind.Unavailable;
        var message = ex.Message.Length <= 400 ? ex.Message : ex.Message[..400] + "...";
        return new AiProviderException(kind, _providerName, message, ex.StatusCode, message, inner: ex);
    }
}
