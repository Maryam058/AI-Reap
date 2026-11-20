namespace AiReap.Infrastructure.Ai;

// Google Gemini API (generativelanguage.googleapis.com) settings. The key is never stored in
// appsettings*.json: supply it via `dotnet user-secrets set "Ai:Gemini:ApiKey" ...` in development
// or the Ai__Gemini__ApiKey environment variable elsewhere.
public class GeminiOptions
{
    public const string SectionName = "Ai:Gemini";

    public string ApiKey { get; set; } = string.Empty;

    // A model available on the Gemini API free tier; change it here or via Ai__Gemini__Model.
    public string Model { get; set; } = "gemini-3.8-flash";

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

    // Requirement generation returns long JSON documents; too low a limit truncates them.
    public int MaxOutputTokens { get; set; } = 16384;

    // Per-attempt timeout for Gemini calls, replacing Ai:Resilience:TimeoutSeconds for Gemini only.
    // A large requirement source (tens of thousands of input tokens, a long JSON answer plus
    // thinking) can legitimately take longer than the shared 120s; Groq's small budgeted requests
    // keep the shared value. 0 uses Ai:Resilience:TimeoutSeconds.
    public int TimeoutSeconds { get; set; } = 300;

    // Total attempts when a Gemini call times out. 1 = no resend: Gemini has usually processed (and
    // counted against the quota) a request it was still working on, so resending a long request
    // spends the quota twice; with a fallback configured the call goes to it instead. 503s, network
    // errors and 429s keep their Ai:Resilience retry rules. 0 uses Ai:Resilience:MaxAttempts.
    public int MaxTimeoutAttempts { get; set; } = 1;

    // Dry-run AI plan only (AiPlanService): requirement generation from a source estimated above
    // this many tokens is flagged as at risk of hitting MaxOutputTokens or TimeoutSeconds when sent
    // to Gemini whole. A size heuristic, not a measurement; it never blocks a request. 0 disables.
    public int LargeGenerationWarningTokens { get; set; } = 30000;

    // Null sends no temperature, so the model default applies. Google advises keeping Gemini 3 models
    // at their default (1.0): lower values can cause looping or degraded output.
    public double? Temperature { get; set; }

    // Gemini 3 thinking effort ("minimal", "low", "medium", "high"); thinking tokens count against
    // MaxOutputTokens. Gemini 3 cannot switch thinking off, and "low" is accepted by every 3.x Flash
    // model, keeping structured extraction fast. Null uses the model's default level.
    public string? ThinkingLevel { get; set; } = "low";

    // Legacy numeric budget for Gemini 2.5 models only (0 turned thinking off there). Ignored when
    // ThinkingLevel is set, because Gemini rejects a request that carries both (HTTP 400).
    public int? ThinkingBudget { get; set; }

    // §26 RAG embeddings (same key). 768 dimensions keeps stored vectors small; changing either
    // value makes existing chunks stale until POST .../documents/reindex re-embeds them.
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";
    public int EmbeddingDimensions { get; set; } = 768;

    // Real Gemini keys are 39 characters ("AIza" + 35). Anything much shorter, or an obvious
    // placeholder, is treated as not configured rather than sent to Google to fail with 400.
    private const int MinimumPlausibleKeyLength = 30;

    public bool HasUsableApiKey =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !ApiKey.Contains("...", StringComparison.Ordinal)
        && !ApiKey.Contains("your", StringComparison.OrdinalIgnoreCase)
        && ApiKey.Trim().Length >= MinimumPlausibleKeyLength;
}
