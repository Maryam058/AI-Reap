namespace AiReap.Application.Ai;

// Optional capability of an IAiChatClient: Groq's request-budget wrapper, and the fallback client
// that may route to it. CompleteAsync fits an over-budget prompt by dropping low-relevance passages
// (PromptBudgetReducer). A caller that would rather split its input than lose any of it calls
// CompleteUncondensedAsync instead: the prompt is sent as built (lossless clean-up at most), or -
// when the provider that would serve it has a per-request budget the prompt cannot fit - an
// AiRequestOverBudgetException carrying that budget is thrown before anything is sent. Which
// provider serves a call is decided per call (FallbackAiChatClient), so the budget is reported at
// that moment rather than exposed up front. Providers without a budget (Gemini) never throw it.
public interface IUncondensedAiChatClient : IAiChatClient
{
    Task<string> CompleteUncondensedAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

public static class AiChatClientExtensions
{
    // Clients without the capability have no request budget: a plain call sends the prompt unchanged.
    public static Task<string> CompleteUncondensedAsync(
        this IAiChatClient client, string systemPrompt, string userPrompt, CancellationToken cancellationToken = default) =>
        client is IUncondensedAiChatClient uncondensed
            ? uncondensed.CompleteUncondensedAsync(systemPrompt, userPrompt, cancellationToken)
            : client.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
}

// Not an AiProviderException on purpose: nothing was sent, so it is never retried, never falls back
// and never reaches the API as a provider failure - the caller (GenerationSupport) splits the input.
public sealed class AiRequestOverBudgetException : Exception
{
    public AiRequestOverBudgetException(
        string provider, int inputBudget, int estimatedTokens,
        int maxRequestTokens = 0, int maxParts = 0, string? maxPartsSetting = null,
        PrimaryProviderOutcome? primary = null, Exception? inner = null)
        : base($"The request (~{estimatedTokens:N0} input tokens) exceeds the {inputBudget:N0}-token input budget of {provider}.", inner)
    {
        Provider = provider;
        InputBudget = inputBudget;
        EstimatedTokens = estimatedTokens;
        MaxRequestTokens = maxRequestTokens;
        MaxParts = maxParts;
        MaxPartsSetting = maxPartsSetting;
        Primary = primary;
    }

    // Set when this provider was serving the call as a fallback (FallbackAiChatClient): why the
    // primary did not serve it, so a refusal can name the actual blocker instead of only this budget.
    public PrimaryProviderOutcome? Primary { get; }

    // The same refusal, recording why the primary provider did not serve the call.
    public AiRequestOverBudgetException WithPrimary(PrimaryProviderOutcome primary) =>
        new(Provider, InputBudget, EstimatedTokens, MaxRequestTokens, MaxParts, MaxPartsSetting, primary, this);

    public string Provider { get; }

    // Input tokens one request to this provider may carry (output reserve already set aside).
    public int InputBudget { get; }

    public int EstimatedTokens { get; }

    // Input plus output one request may use; 0 when unknown.
    public int MaxRequestTokens { get; }

    // The most parts the caller may split the input into for this provider; 0 means no limit.
    public int MaxParts { get; }

    // The setting MaxParts comes from, named in the refusal.
    public string? MaxPartsSetting { get; }
}

// Why the primary provider did not serve a call that went to the fallback. Outcome is built from
// AiProviderException.Message, which provider clients already sanitize (no key, no organization id),
// and is capped so a long provider message cannot flood a log line or an API response.
// Failure is null when the primary was skipped (quota cool-down) rather than attempted.
public sealed record PrimaryProviderOutcome(string Provider, string Outcome, AiProviderException? Failure)
{
    public const int MaxOutcomeLength = 600;

    public static string Cap(string text) => text.Length <= MaxOutcomeLength ? text : text[..MaxOutcomeLength] + "...";
}
