using System.Collections.Concurrent;
using AiReap.Application.Ai;
using Microsoft.Extensions.Logging;

namespace AiReap.Infrastructure.Ai;

// Process-wide "don't call this provider until" marks, so one request's 429 spares the next
// requests (and GenerationSupport's repair attempt) a pointless round trip to a provider whose
// quota is known to be used up. Registered as a singleton.
public sealed class AiProviderCooldown
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCoolingDown(string provider, DateTimeOffset now, out DateTimeOffset until) =>
        _until.TryGetValue(provider, out until) && until > now;

    public void CoolDown(string provider, DateTimeOffset until) =>
        _until.AddOrUpdate(provider, until, (_, existing) => existing > until ? existing : until);
}

// Primary (Gemini) first; the fallback (Groq) only when the primary fails in a way another
// provider can fix: rate limit / quota (429, RESOURCE_EXHAUSTED), 5xx / network outage, or
// timeout. Anything else - bad key, missing configuration, rejected request, blocked content,
// invalid output, caller cancellation - is surfaced as-is: switching providers would hide a
// problem an operator or the user has to fix.
//
// Both inner clients are already ResilientAiChatClient-wrapped; the primary is wrapped without a
// 429 retry (see DependencyInjection) so a quota hit falls back at once instead of spending more
// primary quota. ModelName reports the provider that served the most recent call, which is what
// the pipeline services record in the AI audit trail after CompleteAsync returns (IAiChatClient is
// scoped, and calls within a request are sequential).
//
// CompleteUncondensedAsync routes exactly the same way; each provider is asked for an uncondensed
// call, so a Groq request budget surfaces as AiRequestOverBudgetException (not an
// AiProviderException: it passes through without a fallback) and Gemini is called as usual.
public sealed class FallbackAiChatClient : IUncondensedAiChatClient
{
    private readonly string _primaryName;
    private readonly string _fallbackName;
    private readonly AiProviderCooldown _cooldown;
    private readonly TimeSpan _maxCooldown;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private IAiChatClient _served;

    public FallbackAiChatClient(
        IAiChatClient primary, string primaryName, IAiChatClient fallback, string fallbackName,
        AiProviderCooldown cooldown, TimeSpan maxCooldown, ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        Primary = primary;
        Fallback = fallback;
        _primaryName = primaryName;
        _fallbackName = fallbackName;
        _cooldown = cooldown;
        _maxCooldown = maxCooldown;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _served = primary;
    }

    public IAiChatClient Primary { get; }
    public IAiChatClient Fallback { get; }

    public string ModelName => _served.ModelName;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
        RouteAsync((client, ct) => client.CompleteAsync(systemPrompt, userPrompt, ct), cancellationToken);

    public Task<string> CompleteUncondensedAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
        RouteAsync((client, ct) => client.CompleteUncondensedAsync(systemPrompt, userPrompt, ct), cancellationToken);

    private async Task<string> RouteAsync(Func<IAiChatClient, CancellationToken, Task<string>> send, CancellationToken cancellationToken)
    {
        _served = Primary;

        if (_cooldown.IsCoolingDown(_primaryName, _clock(), out var until))
        {
            _logger.LogWarning(
                "AI provider {Primary} skipped: rate-limited/quota-exhausted until {Until:u}. Using fallback {Fallback}.",
                _primaryName, until, _fallbackName);
            return await CallFallbackAsync($"{_primaryName} was skipped (rate limit/quota cool-down until {until:u})",
                null, send, cancellationToken);
        }

        try
        {
            _logger.LogInformation("AI provider {Primary} attempted (model {Model}).", _primaryName, Primary.ModelName);
            return await send(Primary, cancellationToken);
        }
        catch (AiProviderException ex) when (ShouldFallBack(ex))
        {
            _logger.LogWarning(
                "AI provider {Primary} failed ({Kind}, status {Status}, quotaExhausted {QuotaExhausted}); falling back to {Fallback}.",
                _primaryName, ex.Kind, (int?)ex.ProviderStatusCode, ex.QuotaExhausted, _fallbackName);
            StartCooldown(ex);
            return await CallFallbackAsync($"{_primaryName} failed: {ex.Message}", ex, send, cancellationToken);
        }
    }

    public static bool ShouldFallBack(AiProviderException ex) =>
        ex.Kind is AiFailureKind.RateLimited or AiFailureKind.Unavailable or AiFailureKind.Timeout;

    private void StartCooldown(AiProviderException ex)
    {
        if (ex.Kind != AiFailureKind.RateLimited)
        {
            return;
        }

        // A daily/zero quota with no hint: stay away for the full cap. A per-minute limit without
        // a hint: no cool-down (the next request may well succeed).
        var wait = ex.RetryAfter ?? (ex.QuotaExhausted ? _maxCooldown : TimeSpan.Zero);
        if (wait > _maxCooldown) wait = _maxCooldown;
        if (wait > TimeSpan.Zero)
        {
            _cooldown.CoolDown(_primaryName, _clock() + wait);
        }
    }

    private async Task<string> CallFallbackAsync(
        string primaryOutcome, AiProviderException? primaryFailure,
        Func<IAiChatClient, CancellationToken, Task<string>> send, CancellationToken cancellationToken)
    {
        _served = Fallback;
        try
        {
            var response = await send(Fallback, cancellationToken);
            _logger.LogInformation("AI fallback provider {Fallback} succeeded (model {Model}).", _fallbackName, Fallback.ModelName);
            return response;
        }
        catch (AiProviderException ex)
        {
            _logger.LogError(
                "AI fallback provider {Fallback} also failed ({Kind}, status {Status}).",
                _fallbackName, ex.Kind, (int?)ex.ProviderStatusCode);

            // Both unavailable: report the fallback's failure kind (it decides what the user can
            // do next) but name both providers so neither failure is hidden.
            var bothRateLimited = ex.Kind == AiFailureKind.RateLimited && primaryFailure?.Kind == AiFailureKind.RateLimited;
            throw new AiProviderException(
                ex.Kind, $"{_primaryName} and {_fallbackName}",
                $"{primaryOutcome}. Fallback {_fallbackName} also failed: {ex.Message}",
                ex.ProviderStatusCode, ex.ProviderMessage,
                bothRateLimited ? Earliest(primaryFailure!.RetryAfter, ex.RetryAfter) : ex.RetryAfter,
                ex,
                quotaExhausted: ex.QuotaExhausted && (primaryFailure is null || primaryFailure.QuotaExhausted));
        }
    }

    private static TimeSpan? Earliest(TimeSpan? a, TimeSpan? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}
