using System.Net;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
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
//
// RequestBudgetPolicy (Anthropic): CompleteAsync never condenses either - an over-budget prompt is
// refused before sending; the input budget can be given explicitly (in estimate units, already
// corrected for the provider's tokenizer); and an optional precise counter (Anthropic's free
// count_tokens endpoint) is asked before an estimate-based refusal or split. The default policy is
// exactly the original Groq behavior. StagedPartSourceTokens > 0 advertises staged requirement
// generation (IStagedOutputAiChatClient / AiOutputStaging) for a provider with an output ceiling.
public sealed record RequestBudgetPolicy(
    bool CondenseOverBudget = true,
    int? InputBudgetOverride = null,
    Func<string, string, CancellationToken, Task<int>>? CountInputTokens = null,
    int CountedInputBudget = 0,
    string? TooLargeAdvice = null,
    int StagedPartSourceTokens = 0,
    int StagedMaxParts = 0,
    string? StagedPartSourceSetting = null,
    string? StagedMaxPartsSetting = null)
{
    public static readonly RequestBudgetPolicy Default = new();
}

public sealed class RequestBudgetAiChatClient : IUncondensedAiChatClient, IStagedOutputAiChatClient
{
    private readonly string _providerName;
    private readonly string _budgetSetting;
    private readonly int _maxRequestTokens;
    private readonly int _reservedOutputTokens;
    private readonly string? _maxPartsSetting;
    private readonly ILogger _logger;
    private readonly RequestBudgetPolicy _policy;

    public RequestBudgetAiChatClient(
        IAiChatClient inner, string providerName, string budgetSetting,
        int maxRequestTokens, int reservedOutputTokens, ILogger logger,
        int maxChunkedParts = 0, string? maxPartsSetting = null, RequestBudgetPolicy? policy = null)
    {
        _policy = policy ?? RequestBudgetPolicy.Default;
        Inner = inner;
        _providerName = providerName;
        _budgetSetting = budgetSetting;
        _maxRequestTokens = maxRequestTokens;
        _reservedOutputTokens = Math.Max(0, reservedOutputTokens);
        MaxChunkedParts = Math.Max(0, maxChunkedParts);
        _maxPartsSetting = maxPartsSetting;
        _logger = logger;
    }

    public IAiChatClient Inner { get; }

    public string ModelName => Inner.ModelName;

    // Input tokens available once the output reserve is set aside; <= 0 means unlimited.
    public int InputBudget => _maxRequestTokens <= 0 ? 0
        : _policy.InputBudgetOverride is { } budget ? Math.Max(0, budget)
        : Math.Max(0, _maxRequestTokens - _reservedOutputTokens);

    // False: an over-budget CompleteAsync is refused instead of condensed (no passage is ever dropped).
    public bool CondensesOverBudget => _policy.CondenseOverBudget;

    public RequestBudgetPolicy Policy => _policy;

    // Staged generation limits, read by GenerationSupport and (via AiRouteInspector) the dry-run plan.
    public AiOutputStaging? OutputStaging => _maxRequestTokens <= 0 || _policy.StagedPartSourceTokens <= 0 ? null
        : new AiOutputStaging(_providerName, InputBudget, _maxRequestTokens, _policy.StagedPartSourceTokens,
            Math.Max(1, _policy.StagedMaxParts), _policy.StagedPartSourceSetting ?? "the staged part size",
            _policy.StagedMaxPartsSetting ?? "the staged part limit");

    // The most parts a caller may split an over-budget input into (AiRequestOverBudgetException.MaxParts); 0 = no limit.
    public int MaxChunkedParts { get; }

    // Read by AiRouteInspector, so the dry-run AI plan uses the limits this client enforces.
    public string ProviderName => _providerName;

    public int MaxRequestTokens => _maxRequestTokens;

    public string BudgetSetting => _budgetSetting;

    public string? MaxPartsSetting => _maxPartsSetting;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (_maxRequestTokens <= 0)
        {
            return await Inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }

        var fit = PromptBudgetReducer.Fit(systemPrompt, userPrompt, InputBudget);
        if (!_policy.CondenseOverBudget)
        {
            if (ChunkPlanner.SendsWhole(fit))
            {
                return await SendAsync(systemPrompt, fit, cancellationToken);
            }

            if (await PreciselyFitsAsync(systemPrompt, userPrompt, fit.OriginalTokens, cancellationToken))
            {
                return await SendAsync(systemPrompt, Unchanged(userPrompt, fit), cancellationToken);
            }

            _logger.LogWarning(
                "AI request for {Provider} not sent: ~{Tokens} estimated input tokens exceed the {Budget}-token input budget, " +
                "and this provider's prompts are never condensed.",
                _providerName, fit.OriginalTokens, InputBudget);
            throw TooLarge(fit.OriginalTokens, inner: null);
        }

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
        if (!ChunkPlanner.SendsWhole(fit) && await PreciselyFitsAsync(systemPrompt, userPrompt, fit.OriginalTokens, cancellationToken))
        {
            return await SendAsync(systemPrompt, Unchanged(userPrompt, fit), cancellationToken);
        }

        if (!ChunkPlanner.SendsWhole(fit))
        {
            _logger.LogInformation(
                "AI request for {Provider} not sent uncondensed: ~{Tokens} estimated input tokens exceed the {Budget}-token input budget; " +
                "the caller will split the input.",
                _providerName, fit.OriginalTokens, InputBudget);
            throw new AiRequestOverBudgetException(_providerName, InputBudget, fit.OriginalTokens,
                _maxRequestTokens, MaxChunkedParts, _maxPartsSetting);
        }

        _logger.LogInformation(
            "AI request budget for {Provider}: ~{Tokens} estimated input tokens sent uncondensed (input budget {Budget}).",
            _providerName, fit.FinalTokens, InputBudget);
        return await SendAsync(systemPrompt, fit, cancellationToken);
    }

    // The prompt exactly as the caller built it (no clean-up), for a request the precise count accepted.
    private static PromptReduction Unchanged(string userPrompt, PromptReduction fit) =>
        fit with { UserPrompt = userPrompt, Fits = true, WasReduced = false, FinalTokens = fit.OriginalTokens, KeptPassages = fit.TotalPassages };

    // Only when the policy has a precise counter and the offline estimate says "over budget": the
    // estimate is deliberately conservative, so a request it rejects may still fit. A failed count is
    // logged and the estimate decides (the request is then split or refused, never shortened).
    private async Task<bool> PreciselyFitsAsync(string systemPrompt, string userPrompt, int estimatedTokens, CancellationToken cancellationToken)
    {
        if (_policy.CountInputTokens is not { } count || _policy.CountedInputBudget <= 0)
        {
            return false;
        }

        try
        {
            var counted = await count(systemPrompt, userPrompt, cancellationToken);
            var fits = counted <= _policy.CountedInputBudget;
            _logger.LogInformation(
                "AI request token count for {Provider}: {Counted} input tokens counted by the provider (~{Estimated} estimated offline); " +
                "counted input budget {Budget}; fits {Fits}.",
                _providerName, counted, estimatedTokens, _policy.CountedInputBudget, fits);
            return fits;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "AI request token count for {Provider} failed ({Error}); using the offline estimate (~{Estimated} tokens).",
                _providerName, ex.GetType().Name, estimatedTokens);
            return false;
        }
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
        TooLarge(_providerName, _budgetSetting, _maxRequestTokens, estimatedTokens, inner, _policy.TooLargeAdvice);

    // Also raised by GroqAiChatClient's own last-line check, so both refusals read the same.
    internal static AiProviderException TooLarge(
        string providerName, string budgetSetting, int maxRequestTokens, int estimatedTokens, Exception? inner, string? advice = null)
    {
        var message =
            $"The requirement source is too large for the configured {providerName} request budget " +
            $"({budgetSetting}={maxRequestTokens} tokens; this request needs about {estimatedTokens:N0}). " +
            (advice ?? "Split the source into smaller requirement sources, or retry when the primary AI provider is available again.");
        return new AiProviderException(AiFailureKind.RequestRejected, providerName, message,
            HttpStatusCode.RequestEntityTooLarge, message, inner: inner);
    }
}
