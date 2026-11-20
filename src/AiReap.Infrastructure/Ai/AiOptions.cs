namespace AiReap.Infrastructure.Ai;

// Selects the chat provider AddInfrastructure wires up (see ADR-001 §1 and docs/CLAUDE_SETUP.md).
// Anthropic Claude is the only chat provider: "Anthropic" (the default) is the only accepted value.
// Gemini and Groq are no longer chat providers and never serve a chat request, as primary or as
// fallback; a configuration still naming them fails with an actionable error instead of being
// silently honored or silently replaced. Embeddings are selected separately (EmbeddingProvider).
public class AiOptions
{
    public const string SectionName = "Ai";

    public const string Gemini = "Gemini";
    public const string Groq = "Groq";
    public const string Anthropic = "Anthropic";
    public const string OpenAI = "OpenAI";
    public const string Stub = "Stub";

    public string Provider { get; set; } = Anthropic;

    // Retired. There is no chat fallback: blank or "None" is the only meaningful value. Any other
    // value is ignored with a warning (never used to route a request).
    public string? FallbackProvider { get; set; }

    // Retired with the Gemini/Groq fallback; kept so existing configuration still binds.
    public int PrimaryCooldownSeconds { get; set; } = 600;

    // §26 RAG embeddings, chosen independently of the chat provider so moving chat to Claude never
    // changes which embedding model is used (DocumentChunk.EmbeddingModel is what retrieval filters
    // on). Blank = automatic: Gemini when Ai:Gemini:ApiKey is usable, else OpenAI when
    // Ai:OpenAI:ApiKey is set, else the deterministic stub. Explicit values: "Gemini", "OpenAI", "Stub".
    public string? EmbeddingProvider { get; set; }
}
