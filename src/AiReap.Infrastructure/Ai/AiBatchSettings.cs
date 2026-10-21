using AiReap.Application.Ai.Pipeline;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Ai;

// Ai:Groq:MaxBatchedRunParts (GroqOptions) for the Application layer; negative values count as 0.
public sealed class AiBatchSettings : IAiBatchSettings
{
    private readonly IOptions<GroqOptions> _groq;

    public AiBatchSettings(IOptions<GroqOptions> groq)
    {
        _groq = groq;
    }

    public int MaxBatchedRunParts => Math.Max(0, _groq.Value.MaxBatchedRunParts);
}
