using System.Security.Cryptography;
using System.Text;
using AiReap.Application.Ai;

namespace AiReap.Infrastructure.Ai;

// Registered when no Ai:OpenAI:ApiKey is configured (mirrors StubAiChatClient), so document
// upload/chunking/retrieval and the Copilot pipeline are exercisable with no external
// dependency. The vectors are deterministic hashes of overlapping word shingles, not a
// learned embedding - retrieval will run and return *something*, but it is not semantically
// meaningful. Configure a real key for actual RAG quality.
public class StubEmbeddingClient : IEmbeddingClient
{
    private const int Dimensions = 64;

    public string ModelName => "stub-hash-embedding";

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vector = new float[Dimensions];
        var words = text.ToLowerInvariant().Split(
            new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word));
            var bucket = BitConverter.ToUInt32(hash, 0) % Dimensions;
            var sign = (hash[4] & 1) == 0 ? 1f : -1f;
            vector[bucket] += sign;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return Task.FromResult(vector);
    }
}
