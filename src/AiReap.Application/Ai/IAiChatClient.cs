namespace AiReap.Application.Ai;

// The one seam every AI-provider integration goes through (§30: "AI-provider integration
// must be replaceable without rewriting business logic"). Application services depend only
// on this interface; AiReap.Infrastructure supplies the concrete provider (see ADR-001 §1).
public interface IAiChatClient
{
    string ModelName { get; }

    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}
