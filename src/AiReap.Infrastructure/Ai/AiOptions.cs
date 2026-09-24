namespace AiReap.Infrastructure.Ai;

// Selects which IAiChatClient implementation AddInfrastructure wires up (see ADR-001 §1).
// "Gemini" (default) calls the Google Gemini API; "Anthropic" and "Ollama" remain selectable
// implementations of the same seam but are not required. Extends the existing provider-selection
// mechanism in DependencyInjection.ResolveAiChatClient rather than adding a second one.
public class AiOptions
{
    public const string SectionName = "Ai";

    public const string Gemini = "Gemini";
    public const string Anthropic = "Anthropic";
    public const string Ollama = "Ollama";

    public string Provider { get; set; } = Gemini;
}
