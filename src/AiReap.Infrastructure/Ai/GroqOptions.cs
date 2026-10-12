namespace AiReap.Infrastructure.Ai;

// Groq API (OpenAI-compatible chat completions) settings - the fallback LLM behind Gemini. The key is
// never stored in appsettings*.json: supply it via `dotnet user-secrets set "Ai:Groq:ApiKey" ...`,
// the Ai__Groq__ApiKey environment variable, or the conventional GROQ_API_KEY environment variable
// (used only when Ai:Groq:ApiKey is unset).
public class GroqOptions
{
    public const string SectionName = "Ai:Groq";
    public const string ConventionalKeyVariable = "GROQ_API_KEY";

    public string ApiKey { get; set; } = string.Empty;

    // 131k context, JSON mode and strong reasoning on Groq; change it here or via Ai__Groq__Model.
    public string Model { get; set; } = "openai/gpt-oss-120b";

    public string BaseUrl { get; set; } = "https://api.groq.com/";

    // Same headroom as Gemini: requirement generation returns long JSON documents.
    public int MaxCompletionTokens { get; set; } = 16384;

    // The largest single request the Groq account accepts, in tokens. On the free/on_demand tier this
    // is the model's tokens-per-minute limit (8,000 for openai/gpt-oss-120b): Groq answers 413 to any
    // bigger request. Prompts over budget are condensed by RequestBudgetAiChatClient before sending.
    // Raise it on a paid tier; 0 disables the check.
    public int MaxRequestTokens { get; set; } = 8000;

    // Part of MaxRequestTokens kept free for the response (JSON output + gpt-oss reasoning), which
    // counts against the same per-minute window. Input budget = MaxRequestTokens - ReservedOutputTokens.
    public int ReservedOutputTokens { get; set; } = 4000;

    // Total attempts on a 429 from Groq (overrides Ai:Resilience:MaxRateLimitAttempts when higher).
    // Requests are sent back to back (a repair attempt, chunked parts) against an 8,000 TPM window,
    // and Groq's sub-second retry hints rarely clear it, so a few backed-off retries are allowed.
    public int MaxRateLimitAttempts { get; set; } = 4;

    // Null sends no temperature, so the model default applies.
    public double? Temperature { get; set; }

    // gpt-oss reasoning effort ("low", "medium", "high"); reasoning tokens count against
    // MaxCompletionTokens and the per-minute token limit. Null omits it (required for models
    // that don't accept these values).
    public string? ReasoningEffort { get; set; } = "low";

    // Real Groq keys are 56 characters ("gsk_" + 52). Anything much shorter, or an obvious
    // placeholder, is treated as not configured rather than sent to Groq to fail with 401.
    private const int MinimumPlausibleKeyLength = 30;

    public bool HasUsableApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !ApiKey.Contains("...", StringComparison.Ordinal)
        && !ApiKey.Contains("your", StringComparison.OrdinalIgnoreCase)
        && ApiKey.Trim().Length >= MinimumPlausibleKeyLength;
}
