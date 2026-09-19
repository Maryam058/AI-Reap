namespace AiReap.Application.Ai;

// Mirrors IAiChatClient: business logic depends only on this interface, so the embedding
// provider (OpenAI, a local model, a future Anthropic offering) is swappable without touching
// the RAG pipeline. See ADR-001 §2.
public interface IEmbeddingClient
{
    string ModelName { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
