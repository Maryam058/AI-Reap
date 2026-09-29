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
    public AiRequestOverBudgetException(string provider, int inputBudget, int estimatedTokens)
        : base($"The request (~{estimatedTokens:N0} input tokens) exceeds the {inputBudget:N0}-token input budget of {provider}.")
    {
        Provider = provider;
        InputBudget = inputBudget;
        EstimatedTokens = estimatedTokens;
    }

    public string Provider { get; }

    // Input tokens one request to this provider may carry (output reserve already set aside).
    public int InputBudget { get; }

    public int EstimatedTokens { get; }
}
