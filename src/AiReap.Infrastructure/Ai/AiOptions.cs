namespace AiReap.Infrastructure.Ai;

// Selects which IAiChatClient implementation AddInfrastructure wires up (see ADR-001 §1).
// "Gemini" (default) is the primary cloud LLM and "Groq" the automatic fallback used when Gemini is
// rate-limited, out of quota or temporarily unavailable; "Anthropic" remains a selectable
// implementation of the same seam but is not required. Extends the existing provider-selection
// mechanism in DependencyInjection.ResolveAiChatClient rather than adding a second one.
public class AiOptions
{
    public const string SectionName = "Ai";

    public const string Gemini = "Gemini";
    public const string Groq = "Groq";
    public const string Anthropic = "Anthropic";

    public string Provider { get; set; } = Gemini;

    // Used automatically (no config change needed) when the primary provider fails with a rate
    // limit, exhausted quota, 5xx/network error or timeout. Blank or "None" disables fallback.
    public string? FallbackProvider { get; set; } = Groq;

    // After the primary answers 429, requests go straight to the fallback for the provider's
    // retry delay (or this long, for a daily/zero-limit quota with no delay given), capped here -
    // so an exhausted daily quota isn't probed again on every call.
    public int PrimaryCooldownSeconds { get; set; } = 600;
}
