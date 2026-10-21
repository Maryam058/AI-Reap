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

    // The most parts a source split for Groq may be sent in (GenerationSupport.CallAiAndParseChunkedAsync).
    // Each part can use the whole MaxRequestTokens budget - on the free tier, a whole minute's tokens -
    // so a source needing more parts is refused before any part is sent, instead of spending the
    // daily quota on a run unlikely to finish. 0 disables the check.
    public int MaxChunkedParts { get; set; } = 8;

    // Batch mode (AiChunkRunService). 0 (default) = off: nothing changes - normal Analyze/Generate
    // refuse a source needing more than MaxChunkedParts parts, the batched-run endpoints answer 400
    // batch_mode_disabled, and the AiChunkRuns tables are never queried.
    // N > 0 = on: normal Analyze/Generate are unchanged (still refused above MaxChunkedParts); in
    // addition a batched run of up to N parts in total can be started for such a source. Each
    // Continue action of a run sends at most MaxChunkedParts parts (N when MaxChunkedParts is 0),
    // keeps every part that succeeded, and nothing is saved as artifacts until Commit.
    public int MaxBatchedRunParts { get; set; }

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
