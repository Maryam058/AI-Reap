using System.Net;
using AiReap.Application.Ai;
using Microsoft.Extensions.Logging;

namespace AiReap.Infrastructure.Ai;

// Keeps every request to a provider within that provider's per-request size limit (Groq's free tier
// rejects any request above its tokens-per-minute limit with 413, whatever the output settings). The
// prompt is condensed by PromptBudgetReducer only when it is over budget; a prompt that fits is
// passed through untouched. Wraps the provider's ResilientAiChatClient (DependencyInjection), so it
// runs once per call and retries reuse the condensed prompt. Used for Groq only - Gemini's requests
// are never altered, and FallbackAiChatClient stays purely about choosing a provider.
//
// A request that cannot be made to fit, or that the provider still rejects as too large, fails with
// one clear RequestRejected error: that kind is never retried (ResilientAiChatClient), never falls
// back, and never triggers GenerationSupport's repair attempt, so an oversized request is sent at
// most once.
//
// CompleteUncondensedAsync (IUncondensedAiChatClient) never condenses: a prompt that does not fit
// the budget losslessly is refused with AiRequestOverBudgetException before anything is sent, so
// the caller can split it (GenerationSupport.CallAiAndParseChunkedAsync) instead of losing passages.
public sealed class RequestBudgetAiChatClient : IUncondensedAiChatClient
{
    private readonly string _providerName;
    private readonly string _budgetSetting;
    private readonly int _maxRequestTokens;
    private readonly int _reservedOutputTokens;
    private readonly ILogger _logger;

    public RequestBudgetAiChatClient(
        IAiChatClient inner, string providerName, string budgetSetting,
        int maxRequestTokens, int reservedOutputTokens, ILogger logger)
    {
        Inner = inner;
        _providerName = providerName;
        _budgetSetting = budgetSetting;
        _maxRequestTokens = maxRequestTokens;
        _reservedOutputTokens = Math.Max(0, reservedOutputTokens);
        _logger = logger;
    }

    public IAiChatClient Inner { get; }

    public string ModelName => Inner.ModelName;

    // Input tokens available once the output reserve is set aside; <= 0 means unlimited.
    public int InputBudget => _maxRequestTokens <= 0 ? 0 : Math.Max(0, _maxRequestTokens - _reservedOutputTokens);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (_maxRequestTokens <= 0)
        {
            return await Inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }

        var fit = PromptBudgetReducer.Fit(systemPrompt, userPrompt, InputBudget);
        _logger.LogInformation(
            "AI request budget for {Provider}: ~{Original} estimated input tokens before reduction, ~{Final} after " +
            "(MaxRequestTokens {MaxRequestTokens}, ReservedOutputTokens {ReservedOutputTokens}, input budget {Budget}, fits {Fits}).",
            _providerName, fit.OriginalTokens, fit.FinalTokens, _maxRequestTokens, _reservedOutputTokens, InputBudget, fit.Fits);

        if (!fit.Fits)
        {
            _logger.LogWarning(
                "AI request for {Provider} not sent: ~{Tokens} input tokens cannot be reduced to the {Budget}-token input budget.",
                _providerName, fit.OriginalTokens, InputBudget);
            throw TooLarge(fit.OriginalTokens, inner: null);
        }

        if (fit.WasReduced)
        {
            _logger.LogWarning(
                "AI request for {Provider} condensed to fit its request budget: ~{Original} -> ~{Final} input tokens (budget {Budget}); " +
                "kept {Kept} of {Total} source passages, omitted {Omitted}.",
                _providerName, fit.OriginalTokens, fit.FinalTokens, InputBudget, fit.KeptPassages, fit.TotalPassages, fit.OmittedPassages);
        }

        return await SendAsync(systemPrompt, fit, cancellationToken);
    }

    public async Task<string> CompleteUncondensedAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (_maxRequestTokens <= 0)
        {
            return await Inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }

        // Lossless clean-up (whitespace, duplicated passages, repeated page headers) is allowed;
        // dropping any passage is not.
        var fit = PromptBudgetReducer.Fit(systemPrompt, userPrompt, InputBudget);
        if (!fit.Fits || fit.OmittedPassages > 0)
        {
            _logger.LogInformation(
                "AI request for {Provider} not sent uncondensed: ~{Tokens} estimated input tokens exceed the {Budget}-token input budget; " +
                "the caller will split the input.",
                _providerName, fit.OriginalTokens, InputBudget);
            throw new AiRequestOverBudgetException(_providerName, InputBudget, fit.OriginalTokens);
        }

        _logger.LogInformation(
            "AI request budget for {Provider}: ~{Tokens} estimated input tokens sent uncondensed (input budget {Budget}).",
            _providerName, fit.FinalTokens, InputBudget);
        return await SendAsync(systemPrompt, fit, cancellationToken);
    }

    private async Task<string> SendAsync(string systemPrompt, PromptReduction fit, CancellationToken cancellationToken)
    {
        try
        {
            return await Inner.CompleteAsync(systemPrompt, fit.UserPrompt, cancellationToken);
        }
        catch (AiProviderException ex) when (ex.ProviderStatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            // The provider counts tokens differently than our estimate. Report it plainly instead of
            // passing through the provider's raw message (it names the account's organization).
            _logger.LogWarning(
                "AI provider {Provider} rejected a ~{Tokens}-token request as too large (HTTP 413).", _providerName, fit.FinalTokens);
            throw TooLarge(fit.OriginalTokens, ex);
        }
    }

    private AiProviderException TooLarge(int estimatedTokens, Exception? inner) =>
        TooLarge(_providerName, _budgetSetting, _maxRequestTokens, estimatedTokens, inner);

    // Also raised by GroqAiChatClient's own last-line check, so both refusals read the same.
    internal static AiProviderException TooLarge(
        string providerName, string budgetSetting, int maxRequestTokens, int estimatedTokens, Exception? inner)
    {
        var message =
            $"The requirement source is too large for the configured {providerName} request budget " +
            $"({budgetSetting}={maxRequestTokens} tokens; this request needs about {estimatedTokens:N0}). " +
            "Split the source into smaller requirement sources, or retry when the primary AI provider is available again.";
        return new AiProviderException(AiFailureKind.RequestRejected, providerName, message,
            HttpStatusCode.RequestEntityTooLarge, message, inner: inner);
    }
}
