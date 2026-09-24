namespace AiReap.Infrastructure.Ai;

// Selects which IAiChatClient implementation AddInfrastructure wires up (see ADR-001 §1).
// "Anthropic" (default, prod-safe) preserves existing behavior unchanged; "Ollama" runs a
// fully local model via Ollama's HTTP API with no external API cost - see
// docs/OLLAMA_SETUP.md. Extends the existing provider-selection mechanism in
// DependencyInjection.ResolveAiChatClient rather than adding a second one.
public class AiOptions
{
    public const string SectionName = "Ai";

    public string Provider { get; set; } = "Anthropic";
}
