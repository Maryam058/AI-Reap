namespace AiReap.Infrastructure.Ai;

// Google Gemini API (generativelanguage.googleapis.com) settings. The key is never stored in
// appsettings*.json: supply it via `dotnet user-secrets set "Ai:Gemini:ApiKey" ...` in development
// or the Ai__Gemini__ApiKey environment variable elsewhere.
public class GeminiOptions
{
    public const string SectionName = "Ai:Gemini";

    public string ApiKey { get; set; } = string.Empty;

    // A model available on the Gemini API free tier; change it here or via Ai__Gemini__Model.
    public string Model { get; set; } = "gemini-3.6-flash";

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

    // Requirement generation returns long JSON documents; too low a limit truncates them.
    public int MaxOutputTokens { get; set; } = 16384;

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
