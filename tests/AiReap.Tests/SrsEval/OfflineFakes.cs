using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Common;
using AiReap.Application.Knowledge;
using AiReap.Domain.Common;

namespace AiReap.Tests.SrsEval;

// Phase 0 — the smallest stand-ins the real analysis and generation services need to run fully offline
// (T3 replay): no SQL Server, no network, no AI provider. The chat client only returns the scripted
// responses it is given, in order, and fails the test if asked for more.
public sealed class ScriptedAiChatClient : IAiChatClient
{
    private readonly Queue<string> _responses;

    public ScriptedAiChatClient(params string[] responses) => _responses = new Queue<string>(responses);

    public string ModelName => "scripted-offline";

    public List<(string System, string User)> Prompts { get; } = [];

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add((systemPrompt, userPrompt));
        return _responses.Count > 0
            ? Task.FromResult(_responses.Dequeue())
            : throw new InvalidOperationException("The scripted AI client has no response left: the service made an unexpected AI call.");
    }
}

// A Business Analyst: may read raw sources and run analysis and generation.
public sealed class OfflineCurrentUser : ICurrentUser
{
    public string UserId => "phase0-offline-user";

    public bool IsInRole(string role) => role == Roles.BusinessAnalyst;
}

public sealed class AllowAllProjectAccess : IProjectAccessService
{
    public Task<bool> IsMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task EnsureMemberAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

// No project documents: generation runs without excerpts.
public sealed class NoDocumentsRetriever : IKnowledgeRetriever
{
    public Task<RetrievalResult> RetrieveAsync(Guid projectId, string query, int topK, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RetrievalResult([], 0, false));

    public Task<IReadOnlyList<RetrievedChunk>> LargestCandidatesAsync(Guid projectId, int topK, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
}

// Batch mode off, as in the default configuration.
public sealed class BatchModeOff : IAiBatchSettings
{
    public int MaxBatchedRunParts => 0;
}
